namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;

/// <summary>
/// 振替伝票の一覧（入力の一覧）の取引先名が、<b>計上済みは写し・下書きは現在名</b>になること。
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は宣言と SQL の整合しか見ないので、
/// <c>COALESCE(NULLIF(...), ...)</c> の 3 段のどこを取り違えても緑になる。
/// <b>行を入れて読み戻さないと分からない</b>（qa/03 L-03 の型）。</para>
/// <para>ここが守るのは <see href="../../../docs/decisions/0037-計上済みの伝票は画面でも計上時の姿を見せる.md">ADR-0037</see> §3——
/// <b>同じ伝票を一覧と詳細で見て名前が違わないこと</b>である。</para>
/// </remarks>
public class JournalEntryListQueryTests
{
    /// <summary>
    /// 伝票 3 件。**それぞれ違う理由で違う名前になる**（縮退させない）。
    /// 1) 計上済み・写しあり  2) 計上済み・写しが空文字  3) 下書き（写しは無い）
    /// </summary>
    private const string Entries = """
        INSERT INTO partners (code, name) VALUES ('P002', 'いまのマスタ名');
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, partner_id, partner_name_snapshot, entered_at)
            VALUES (1, '2026-05-10', '2026-05-10', 'draft', 'normal', 2, '計上したときの名前', '2026-05-10 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 1, 'debit', 1, 1000, 1);

        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, partner_id, partner_name_snapshot, entered_at)
            VALUES (1, '2026-05-11', '2026-05-11', 'draft', 'normal', 2, '', '2026-05-11 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 1, 'debit', 1, 2000, 1);

        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, partner_id, entered_at)
            VALUES (1, '2026-05-12', '2026-05-12', 'draft', 'normal', 2, '2026-05-12 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 1, 'debit', 1, 3000, 1);

        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-10 11:00:00' WHERE id = 1;
        UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-11 11:00:00' WHERE id = 2;
        """;

    [Fact]
    public void 計上済みは写しを出し_下書きは現在のマスタ名を出す()
    {
        using var db = Create();

        var rows = Run(db).ToDictionary(r => r.EntryId, r => r.PartnerName);

        Assert.Equal("計上したときの名前", rows[1]);

        // **空文字は「無い」として扱う**（NULLIF）。帳簿の空値検索と同じ見方に揃えてある——
        // 素通しにすると、同じ行が一覧では空欄・帳簿では現在名になる。
        Assert.Equal("いまのマスタ名", rows[2]);

        // 下書きはまだ写しを持たない。入力の途中なので、いま選べる名前を出すのが正しい。
        Assert.Equal("いまのマスタ名", rows[3]);
    }

    [Fact]
    public void 取引先を改名しても計上済みの行は動かない()
    {
        using var db = Create();
        TestDatabase.Execute(db, "UPDATE partners SET name = '改名したあとの名前' WHERE id = 2");

        var rows = Run(db).ToDictionary(r => r.EntryId, r => r.PartnerName);

        Assert.Equal("計上したときの名前", rows[1]);
        Assert.Equal("改名したあとの名前", rows[3]);
    }

    [Fact]
    public void 取引先の無い伝票は空のまま()
    {
        using var db = Create();
        TestDatabase.Execute(db, "UPDATE journal_entries SET partner_id = NULL WHERE id = 3");

        Assert.Null(Run(db).Single(r => r.EntryId == 3).PartnerName);
    }

    private static SqliteConnection Create()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, Entries);
        return db;
    }

    private sealed record Row(long EntryId, string? PartnerName);

    /// <summary>一覧の SQL を<b>本物のまま</b>流す。渡さなかったパラメータは NULL（＝条件なし）。</summary>
    private static IReadOnlyList<Row> Run(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = File.ReadAllText(TestDatabase.QuerySqlOf("JournalEntryList"));

        foreach (var name in Parameters)
        {
            command.Parameters.AddWithValue(name, DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt64(reader.GetOrdinal("entry_id")),
                reader.IsDBNull(reader.GetOrdinal("partner_name"))
                    ? null : reader.GetString(reader.GetOrdinal("partner_name"))));
        }

        return rows;
    }

    /// <summary>SQL が使う入力パラメータ。<b>足りないと SQLite が実行時に落ちる</b>。</summary>
    private static readonly string[] Parameters =
    [
        "@p_fiscal_year_id", "@p_status", "@p_entry_type",
        "@p_transaction_date_from", "@p_transaction_date_to",
        "@p_posting_date_from", "@p_posting_date_to",
        "@p_entry_no_min", "@p_entry_no_max", "@p_partner_id", "@p_keyword",
    ];
}
