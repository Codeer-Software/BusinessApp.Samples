namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 補助科目は 2 値である（ADR-0038 §3。docs/04 §1 の A-3）。
/// </summary>
/// <remarks>
/// <para><b>使う科目では補助科目が要り、使わない科目は持てない。</b>
/// 関門（<c>JournalEntryValidator</c>）が本体で、ここは<b>関門が走らない経路</b>
/// （CSV 取込・<c>sql</c> CLI・手作業の SQL）への最後の守りである。</para>
/// <para><b>広さは関門に揃えてある</b>（docs/10 §4-2-1 の二層の広さ）——
/// 「持てない」側は<b>取消と訂正では止めない</b>。規則より前に計上された伝票を
/// 打ち消せなくなるからで、取消の明細はシステムが原仕訳から作る（docs/10 §5・ADR-0004）。
/// <b>「要る」側は全種別で止める</b>（そちらは関門も Error である）。</para>
/// <para><b>下書きには求めない。</b> 摘要（<see cref="JournalDescriptionGuardTests"/>）と同じく、
/// 見るのは<b>下書き → 計上の UPDATE</b> だけである。</para>
/// </remarks>
public class JournalSubAccountGuardTests
{
    /// <summary>科目 1 を「補助科目を使う」にし、その補助科目を作る。</summary>
    private const string UsesSubAccount = """
        UPDATE accounts SET uses_sub_account = 1 WHERE id = 1;
        INSERT INTO sub_accounts (account_id, code, name) VALUES (1, 'S001', '本店');
        """;

    /// <summary>科目 1 の補助科目だけ作る（科目は「使わない」のまま）。</summary>
    private const string SubAccountOfUnusedAccount = """
        INSERT INTO sub_accounts (account_id, code, name) VALUES (1, 'S001', '本店');
        """;

    private const string Post =
        "UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00' WHERE id = 1";

    /// <summary>科目 1 の明細に補助科目を付ける／付けない下書きを 1 件書く。</summary>
    private static SqliteConnection Draft(string setup, string? subAccountId, string entryType = "normal")
    {
        // **識別子を明示する**——原仕訳を先に入れる種別があるので、採番に任せると id が 1 でなくなる。
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db, setup);
        if (entryType != "normal")
        {
            // 取消・訂正には原仕訳が要る（外部キーと CHECK）。中身は問わないので下書きで足りる。
            TestDatabase.Execute(db, """
                INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                             description, entered_at)
                    VALUES (99, 1, '2026-05-19', '2026-05-19', 'draft', 'normal', '原仕訳', '2026-05-19 10:00:00');
                """);
        }

        TestDatabase.Execute(db, $"""
            INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                         description, entered_at, original_entry_id)
                VALUES (1, 1, '2026-05-20', '2026-05-20', 'draft', '{entryType}', '5 月分の現金売上',
                        '2026-05-20 10:00:00', {(entryType == "normal" ? "NULL" : "99")});
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, sub_account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, {subAccountId ?? "NULL"}, 100000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (1, 2, 'credit', 2, 2, 100000, 1);
            """);
        return db;
    }

    private static string StatusOf(SqliteConnection db)
        => TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1");

    [Fact]
    public void 補助科目を使わない科目の明細に補助科目を付けたままでは計上できない()
    {
        using var db = Draft(SubAccountOfUnusedAccount, "1");

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使わない勘定科目の明細に補助科目は付けられない", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 補助科目を使う科目の明細に補助科目が無ければ計上できない()
    {
        using var db = Draft(UsesSubAccount, subAccountId: null);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使う勘定科目の明細には補助科目が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 補助科目を使う科目に補助科目が付いていれば計上できる()
    {
        using var db = Draft(UsesSubAccount, "1");

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Fact]
    public void 使わない科目でも補助科目が空なら計上できる()
    {
        using var db = Draft(SubAccountOfUnusedAccount, subAccountId: null);

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Theory]
    [InlineData("reversal")]
    [InlineData("correction")]
    public void 取消と訂正は補助科目が付いていても計上できる(string entryType)
    {
        // **規則より前に計上された伝票を打ち消せなくなってはいけない**（docs/10 §5・ADR-0004）。
        // 関門も同じ広さで、こちらは警告に留める。
        using var db = Draft(SubAccountOfUnusedAccount, "1", entryType);

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Theory]
    [InlineData("reversal")]
    [InlineData("correction")]
    public void 取消と訂正でも補助科目が要る側は止める(string entryType)
    {
        // **「要る」側は全種別で止める。** 原仕訳が補助科目を持っていれば取消にも写るので、
        // ここが鳴るのは原仕訳の側が壊れている場合だけである。
        using var db = Draft(UsesSubAccount, subAccountId: null, entryType);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("補助科目を使う勘定科目の明細には補助科目が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 下書きのままなら補助科目を付けたままにできる()
    {
        // **打ちかけの伝票を止めない。** ここが赤くなったら、下書きにまで規則を広げてしまっている。
        using var db = Draft(SubAccountOfUnusedAccount, subAccountId: null);

        TestDatabase.Execute(db, "UPDATE journal_lines SET sub_account_id = 1 WHERE id = 1");

        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT sub_account_id FROM journal_lines WHERE id = 1"));
        Assert.Equal("draft", StatusOf(db));
    }
}
