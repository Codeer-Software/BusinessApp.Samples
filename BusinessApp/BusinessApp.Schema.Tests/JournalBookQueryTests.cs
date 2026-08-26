namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;

/// <summary>
/// 仕訳帳の法定検索が<b>実際に効くこと</b>。
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は宣言と SQL の整合しか見ない。
/// <b>条件が効いているかは、値を入れて行数を数えないと分からない。</b>
/// `&gt;=` と `&gt;` の取り違え、`date()` の掛け忘れ、`p_blank_field` の分岐名の綴り違いは、
/// どれも例外にならず<b>静かに 0 件や全件</b>を返す（qa/03 L-03 の型）。</para>
/// <para>ここが守るのは制度要件そのものである——規則 5 ⑤一ハの (2) 範囲・(3) 組み合わせ、
/// 通達 8-13 の空値検索、8-14 の記録項目、8-15 の課税期間ごとの範囲指定。</para>
/// </remarks>
public class JournalBookQueryTests
{
    /// <summary>
    /// 帳簿に載る素材。取引日・金額・取引先・部門・摘要をすべてずらしてある。
    /// </summary>
    /// <remarks>
    /// <b>区別すべきものを同じ値にしない</b>（qa/03 L-02）。取引日と計上日も必ずずらす。
    /// </remarks>
    private const string Book = """
        INSERT INTO partners (code, name) VALUES ('P002', '乙商事');
        -- 1) 05-10 取引 / 05-12 計上 / 1000 円 / 取引先あり（伝票） / 部門あり / 摘要あり
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, partner_id, entered_at)
            VALUES (1, '2026-05-10', '2026-05-12', 'draft', 'normal', '5 月の売上', 1, '2026-05-12 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id, item_description)
            VALUES (1, 1, 'debit', 1, 2, 1000, 1, '商品 A');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 2, 'credit', 2, 1000, 1);

        -- 2) 05-20 取引 / 05-25 計上 / 5000 円 / 取引先なし / 部門なし / 摘要なし
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-20', '2026-05-25', 'draft', 'normal', '2026-05-25 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 1, 'debit', 1, 5000, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 2, 'credit', 2, 5000, 1);

        -- 3) 下書き。**帳簿には出ない。**
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-15', '2026-05-15', 'draft', 'normal', '2026-05-15 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 1, 'debit', 1, 3000, 1);
        """;

    /// <summary>
    /// 下書きを計上する。<b>計上したあとは何も直せない</b>ので（I-05・DDL のトリガ）、
    /// テストごとの差異は計上より前に入れる。
    /// </summary>
    private const string Post = """
        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-12 10:00:00' WHERE id = 1;
        UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-25 10:00:00' WHERE id = 2;
        """;

    // --- 出す行の範囲 ---

    [Fact]
    public void 条件なしなら計上済みの明細が全部出る()
    {
        using var db = Create();

        // 4 行 = 計上済み 2 伝票 × 明細 2 行。**下書きの 1 行は出ない。**
        Assert.Equal([1L, 1L, 2L, 2L], Run(db).Select(r => r.EntryId));
    }

    [Fact]
    public void 下書きは帳簿に出ない()
    {
        using var db = Create();

        Assert.DoesNotContain(3L, Run(db).Select(r => r.EntryId));
    }

    // --- 取引年月日の範囲（規則 5 ⑤一ハ(2)・通達 8-14）---

    [Theory]
    [InlineData("2026-05-10", "2026-05-20", 4)]   // 両端を含む
    [InlineData("2026-05-11", "2026-05-20", 2)]   // 下端の翌日 → 1 件目が落ちる
    [InlineData("2026-05-10", "2026-05-19", 2)]   // 上端の前日 → 2 件目が落ちる
    [InlineData("2026-05-20", "2026-05-20", 2)]   // 1 日だけ
    [InlineData("2026-05-21", "2026-05-31", 0)]
    public void 取引日の範囲は両端を含む(string from, string to, int expected)
    {
        // **境界の両端を必ず通す。** `>=` を `>` に取り違えても、片側だけのテストでは緑になる。
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_transaction_date_from", from), ("@p_transaction_date_to", to)).Count);
    }

    [Fact]
    public void 取引日で引いても計上日では引かない()
    {
        // 取引日 05-20 の伝票は計上日 05-25。取引日の条件が計上日を見ていたら、これが落ちる。
        using var db = Create();

        Assert.Equal([2L, 2L], Run(db, ("@p_transaction_date_from", "2026-05-16")).Select(r => r.EntryId));
    }

    // --- 取引金額の範囲（通達 8-14）---

    [Theory]
    [InlineData(1000, 5000, 4)]
    [InlineData(1001, 5000, 2)]
    [InlineData(1000, 4999, 2)]
    [InlineData(5000, 5000, 2)]
    [InlineData(5001, 9999, 0)]
    public void 金額の範囲は両端を含む(long min, long max, int expected)
    {
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_amount_min", min), ("@p_amount_max", max)).Count);
    }

    // --- 伝票番号（通達 8-14 (注) の一連番号による検索）---

    [Theory]
    [InlineData(1, 2, 4)]
    [InlineData(2, 2, 2)]
    [InlineData(1, 1, 2)]
    [InlineData(3, 9, 0)]
    public void 伝票番号の範囲は両端を含む(long min, long max, int expected)
    {
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_entry_no_min", min), ("@p_entry_no_max", max)).Count);
    }

    /// <summary>
    /// 片側だけの指定も効く。<b>両方揃ったときだけ効く実装</b>だと、ここが落ちる。
    /// </summary>
    [Fact]
    public void 伝票番号は片側だけでも絞れる()
    {
        using var db = Create();

        Assert.Equal([2L, 2L], Run(db, ("@p_entry_no_min", 2L)).Select(r => r.EntryId));
        Assert.Equal([1L, 1L], Run(db, ("@p_entry_no_max", 1L)).Select(r => r.EntryId));
    }

    /// <summary>
    /// <b>伝票番号は会計年度の中の連番</b>なので、年度を指定しなければ同じ番号が複数の年度から出る。
    /// 年度と組み合わせれば 1 本に絞れる（通達 8-15 の「課税期間ごとに」）。
    /// </summary>
    [Fact]
    public void 伝票番号は会計年度と組み合わせて一意になる()
    {
        using var db = Create();
        TestDatabase.Execute(db, """
            INSERT INTO fiscal_years (code, label, start_date, end_date, status)
                VALUES ('FY19', '第 19 期', '2027-04-01', '2028-03-31', 'open');
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
                VALUES (2, '2027-04-02', '2027-04-02', 'draft', 'reversal', 1, '2027-04-02 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (4, 1, 'credit', 1, 1000, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2027-04-02 10:00:00' WHERE id = 4;
            """);

        // 番号だけで引くと、第 18 期の 1 番（2 行）と第 19 期の 1 番（1 行）が並ぶ。
        Assert.Equal([1L, 1L, 4L], Run(db, ("@p_entry_no_min", 1L), ("@p_entry_no_max", 1L)).Select(r => r.EntryId));

        // 年度を足すと 1 本になる。
        Assert.Equal([4L], Run(db,
            ("@p_entry_no_min", 1L), ("@p_entry_no_max", 1L), ("@p_fiscal_year_id", 2L)).Select(r => r.EntryId));
    }

    // --- 組み合わせ（規則 5 ⑤一ハ(3)・通達 8-15）---

    [Fact]
    public void 課税期間と日付と金額を組み合わせられる()
    {
        // 通達 8-15「課税期間ごとに、日付又は金額の任意の範囲を指定して」。
        using var db = Create();

        var rows = Run(db,
            ("@p_fiscal_year_id", 1L),
            ("@p_transaction_date_from", "2026-05-01"),
            ("@p_transaction_date_to", "2026-05-31"),
            ("@p_amount_min", 2000L));

        Assert.Equal([2L, 2L], rows.Select(r => r.EntryId));
    }

    [Fact]
    public void 別の会計年度を指定すると一件も出ない()
    {
        using var db = Create();

        Assert.Empty(Run(db, ("@p_fiscal_year_id", 99L)));
    }

    // --- 空値検索（通達 8-13）---

    [Theory]
    [InlineData("partner", 2)]            // 取引先が無いのは 2 番の 2 行
    [InlineData("department", 3)]         // 部門があるのは 1 番の 1 行目だけ
    [InlineData("description", 2)]        // 摘要が無いのは 2 番
    [InlineData("item_description", 3)]   // 内容があるのは 1 番の 1 行目だけ
    [InlineData("sub_account", 4)]        // 補助科目はどこにも無い
    public void 記録事項がない行を探せる(string field, int expected)
    {
        // **候補値の綴りが 1 つでもずれると 0 件が静かに返る。** 5 値すべてを通す。
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_blank_field", field)).Count);
    }

    [Fact]
    public void 知らない空値の指定は一件も返さない()
    {
        // 候補と SQL の分岐がずれたときの症状を固定しておく（黙って全件にはしない）。
        using var db = Create();

        Assert.Empty(Run(db, ("@p_blank_field", "unknown_field")));
    }

    // --- 取引先（法定記載事項①）---

    [Fact]
    public void 取引先は伝票の値でも明細の値でも引ける()
    {
        using var db = Create();

        // 1 番は伝票に取引先がある。明細には無い。
        Assert.Equal([1L, 1L], Run(db, ("@p_partner_id", 1L)).Select(r => r.EntryId));
    }

    [Fact]
    public void 明細の取引先は伝票の取引先を上書きする()
    {
        // 表示・検索・空値検索が同じモデルであること。
        // 明細に乙商事を入れたら、帳簿の表示も検索も乙商事に従う。
        using var db = Create(
            "UPDATE journal_lines SET partner_id = 2 WHERE journal_entry_id = 1 AND line_no = 1");

        Assert.Equal("乙商事", Run(db).First(r => r.LineNo == 1).PartnerName);
        Assert.Equal([1L], Run(db, ("@p_partner_id", 2L)).Select(r => r.EntryId));
        Assert.Single(Run(db, ("@p_partner_id", 1L)));
    }

    [Fact]
    public void 取引先名は明細の写しを優先する()
    {
        // 取引先の改名で過去の帳簿の記載が変わらないこと（docs/04 §4-2）。
        using var db = Create(
            "UPDATE journal_lines SET partner_name_snapshot = '株式会社取引先（旧称）' WHERE journal_entry_id = 1 AND line_no = 1");

        Assert.Equal("株式会社取引先（旧称）", Run(db).First(r => r.LineNo == 1).PartnerName);
    }

    // --- 摘要・内容の部分一致 ---

    [Fact]
    public void 摘要でも内容でも引ける()
    {
        using var db = Create();

        Assert.Equal(2, Run(db, ("@p_keyword", "売上")).Count);       // 摘要は伝票の項目なので 2 行
        Assert.Single(Run(db, ("@p_keyword", "商品")));               // 内容は明細の項目なので 1 行
    }

    [Fact]
    public void 打った文字はワイルドカードにならない()
    {
        // 「%」だけを打つと全件に当たる、という壊れ方をしない。
        using var db = Create("UPDATE journal_entries SET description = '値引 10%' WHERE id = 2");

        Assert.Empty(Run(db, ("@p_keyword", "%%")));
        Assert.Equal(2, Run(db, ("@p_keyword", "10%")).Count);
    }

    // --- 並び順（相互関連性の読みやすさ）---

    [Fact]
    public void 取引日_年度_伝票番号_行番号の順に並ぶ()
    {
        // 伝票番号は**年度内**の連番なので、年度を並び順に入れないと
        // 年度をまたぐ取消が原仕訳より前に並ぶ。
        using var db = Create();
        TestDatabase.Execute(db, """
            INSERT INTO fiscal_years (code, label, start_date, end_date, status)
                VALUES ('FY19', '第 19 期', '2027-04-01', '2028-03-31', 'open');
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
                VALUES (2, '2026-05-10', '2027-04-02', 'draft', 'reversal', 1, '2027-04-02 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (4, 1, 'credit', 1, 1000, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2027-04-02 10:00:00' WHERE id = 4;
            """);

        // 取引日が同じ 05-10 の 3 行。第 18 期の #1 が先、第 19 期の #1 が後。
        var sameDay = Run(db).Where(r => r.EntryId is 1 or 4).ToList();
        Assert.Equal([1L, 1L, 4L], sameDay.Select(r => r.EntryId));
    }

    // --- 実行の土台 ---

    /// <param name="beforePosting">
    /// 計上する前に流す SQL。計上済みは変更できないので、行の差異はここで入れる。
    /// </param>
    private static SqliteConnection Create(string beforePosting = "")
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, Book);
        if (beforePosting.Length > 0)
        {
            TestDatabase.Execute(db, beforePosting);
        }

        TestDatabase.Execute(db, Post);
        return db;
    }

    private sealed record Row(long EntryId, long LineNo, string? PartnerName);

    /// <summary>
    /// 仕訳帳の SQL を<b>本物のまま</b>流す。渡さなかったパラメータは NULL（＝条件なし）。
    /// </summary>
    private static IReadOnlyList<Row> Run(SqliteConnection db, params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.CommandText = File.ReadAllText(Path.Combine(
            TestDatabase.ModulesDirectory, "Books", "JournalBook.Query.sql"));

        foreach (var name in Parameters)
        {
            var (givenName, givenValue) = parameters.FirstOrDefault(p => p.Name == name);
            command.Parameters.AddWithValue(name, givenName is null ? DBNull.Value : givenValue);
        }

        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt64(reader.GetOrdinal("entry_id")),
                reader.GetInt64(reader.GetOrdinal("line_no")),
                reader.IsDBNull(reader.GetOrdinal("partner_name"))
                    ? null : reader.GetString(reader.GetOrdinal("partner_name"))));
        }

        return rows;
    }

    /// <summary>
    /// SQL が使う入力パラメータ。<b>足りないと SQLite が実行時に落ちる</b>ので、
    /// 一覧が古くなったことはテストの失敗として現れる。
    /// </summary>
    private static readonly string[] Parameters =
    [
        "@p_fiscal_year_id", "@p_transaction_date_from", "@p_transaction_date_to",
        "@p_amount_min", "@p_amount_max", "@p_entry_no_min", "@p_entry_no_max",
        "@p_account_id", "@p_partner_id", "@p_keyword", "@p_blank_field",
    ];
}
