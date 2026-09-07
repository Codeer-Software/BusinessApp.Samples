namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// I-05 計上済み仕訳は変更も削除もされない（ADR-0004）。
/// </summary>
/// <remarks>
/// AccountingCore の検証とサーバの関門が第一の防波堤だが、これは
/// <b>CSV 取込・API・スクリプト・手作業の SQL のどれからでも通る最後の関門</b>である。
/// 「規則を迂回する経路を作らない」を、規約ではなく DB に守らせていることを確かめる。
/// </remarks>
public class JournalImmutabilityTests
{
    [Fact]
    public void 下書きを作り明細を入れて計上できる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        Assert.Equal("posted", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT entry_no FROM journal_entries WHERE id = 1"));
        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 1"));
    }

    [Fact]
    public void 下書きは自由に編集できる()
    {
        using var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 100, 1);
            UPDATE journal_lines SET amount = 200 WHERE id = 1;
            UPDATE journal_entries SET description = '書き直した' WHERE id = 1;
            DELETE FROM journal_lines WHERE id = 1;
            DELETE FROM journal_entries WHERE id = 1;
            """);

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries"));
    }

    [Theory]
    [InlineData("UPDATE journal_entries SET description = '改ざん' WHERE id = 1")]
    [InlineData("UPDATE journal_entries SET status = 'draft' WHERE id = 1")]
    [InlineData("DELETE FROM journal_entries WHERE id = 1")]
    [InlineData("UPDATE journal_lines SET amount = 1 WHERE journal_entry_id = 1")]
    [InlineData("UPDATE journal_lines SET account_id = 2 WHERE journal_entry_id = 1")]
    [InlineData("DELETE FROM journal_lines WHERE journal_entry_id = 1")]
    public void 計上済みの仕訳は変更も削除もできない(string sql)
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));
    }

    [Fact]
    public void 計上済みの仕訳に明細を追加できない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 3, 'debit', 1, 1, 1);
            """));
    }

    /// <summary>
    /// <b>下書きの明細を、計上済みの伝票へ付け替えられない。</b>
    /// </summary>
    /// <remarks>
    /// 上の 2 本のトリガは <c>OLD</c> の伝票（＝いま所属している伝票）しか見ないので、
    /// <b>この経路だけが 2026-09-03 まで開いていた</b>（qa/03 L-25）。通ると、計上済みの伝票に
    /// 身に覚えのない行が増える——貸借一致（I-01）も不変性（I-05）もその瞬間に破れ、
    /// しかも計上済みなので訂正も取消もできない行が恒久的に残る。
    /// </remarks>
    [Fact]
    public void 下書きの明細を計上済みの伝票へ移せない()
    {
        using var db = WithPostedAndDraft();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE journal_lines SET journal_entry_id = 1 WHERE journal_entry_id = 2"));

        // **どの制約で落ちたかまで見る。** 例外の型だけだと、seed の形が変わって
        // 別の制約（UNIQUE(journal_entry_id, line_no) など）に当たっても緑のままになる。
        Assert.Contains("計上済みの仕訳へ明細を移動できない", thrown.Message, StringComparison.Ordinal);

        // 計上済みは増えず、下書きの側も残っている（＝文が丸ごと巻き戻った）。
        Assert.Equal(2L, TestDatabase.ScalarOf<long>(
            db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 1"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(
            db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 2"));
    }

    [Fact]
    public void 計上済みの明細を下書きへ逃がせない()
    {
        using var db = WithPostedAndDraft();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE journal_lines SET journal_entry_id = 2 WHERE journal_entry_id = 1"));

        Assert.Contains("計上済みの仕訳明細は変更できない", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>REPLACE の暗黙の DELETE で、計上済みが音もなく消えない。</b>
    /// </summary>
    /// <remarks>
    /// SQLite は、REPLACE が制約充足のために消す行の DELETE トリガを
    /// <c>PRAGMA recursive_triggers</c> が OFF のあいだ発火しない（既定は OFF）。
    /// <b>接続の設定に頼らず、衝突そのものを拒む</b>（qa/03 L-26）。
    /// </remarks>
    [Theory]
    [InlineData(
        "INSERT OR REPLACE INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at) "
        + "VALUES (1, 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00')",
        "計上済みの仕訳を上書きできない")]
    [InlineData(
        "UPDATE OR REPLACE journal_entries SET idempotency_key = 'K1' WHERE id = 2",
        "計上済みの仕訳を上書きできない")]
    [InlineData(
        "INSERT OR REPLACE INTO journal_lines (id, journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id) "
        + "VALUES (1, 2, 5, 'debit', 1, 7, 1)",
        "計上済みの仕訳明細を上書きできない")]
    [InlineData(
        "UPDATE OR REPLACE journal_lines SET id = 1 WHERE id = 3",
        "計上済みの仕訳明細を上書きできない")]
    public void REPLACEで計上済みを置き換えられない(string sql, string message)
    {
        using var db = WithPostedAndDraft();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));

        Assert.Contains(message, thrown.Message, StringComparison.Ordinal);
        Assert.Equal("posted", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
        Assert.Equal(2L, TestDatabase.ScalarOf<long>(
            db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 1"));
    }

    /// <summary>
    /// 計上済みの伝票 1 件（id = 1・明細 2 行・冪等キー <c>K1</c>）と、
    /// 下書き 1 件（id = 2・明細 1 行＝id 3）。
    /// </summary>
    private static SqliteConnection WithPostedAndDraft()
    {
        var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (2, 9, 'credit', 1, 999, 1);
            """);

        // 冪等キーは計上済みに付ける。計上済みは UPDATE できないので、トリガを外して書く
        // ——**この経路はテストの都合であって、製品には無い**。
        TestDatabase.Execute(db, """
            DROP TRIGGER trg_journal_entries_posted_no_update;
            UPDATE journal_entries SET idempotency_key = 'K1' WHERE id = 1;
            CREATE TRIGGER trg_journal_entries_posted_no_update
            BEFORE UPDATE ON journal_entries
            FOR EACH ROW WHEN OLD.status = 'posted'
            BEGIN
                SELECT RAISE(ABORT, '計上済みの仕訳は変更できない。訂正・取消は反対仕訳で行う。');
            END;
            """);

        return db;
    }

    /// <summary>訂正・取消は反対仕訳で行う。原仕訳は残したまま新しい伝票が増える。</summary>
    [Fact]
    public void 取消は反対仕訳として別の伝票になる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-21', '2026-05-21', 'draft', 'reversal', 1, '2026-05-21 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (2, 1, 'credit', 1, 100000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (2, 2, 'debit', 2, 2, 100000, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-21 10:00:00' WHERE id = 2;
            """);

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT original_entry_id FROM journal_entries WHERE id = 2"));
    }

    /// <summary>
    /// 計上は「下書きとして書いてから状態を進める」経路しか無い。
    /// </summary>
    /// <remarks>
    /// ここが開いていると、貸借不一致・明細ゼロの計上済み伝票を直接書き込めてしまう。
    /// しかも他のトリガが UPDATE も DELETE も明細の追加も止めるので、
    /// <b>訂正も取消もできない行が恒久的に残る</b>（ADR-0004 が最も避けたい状態）。
    /// </remarks>
    [Fact]
    public void 最初から計上済みとして仕訳を作れない()
    {
        using var db = SchemaSeed.Create();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, entry_no, transaction_date, posting_date, status, entry_type, entered_at, posted_at)
                VALUES ('5 月分の現金売上', 1, 99, '2026-05-20', '2026-05-20', 'posted', 'normal', '2026-05-20 10:00:00', '2026-05-20 10:00:00');
            """));

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries"));
    }

    /// <summary>
    /// 金額は整数円でなければならない（docs/10 §3）。
    /// </summary>
    /// <remarks>
    /// <b>INTEGER と書くだけでは整数にならない。</b> SQLite の型親和性は 100.5 を整数に落とせず、
    /// REAL のまま格納する。貸借一致の判定と保存値がずれる（I-01）ので DB でも拒む。
    /// </remarks>
    [Fact]
    public void 小数の金額は保存できない()
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 100.5, 1);
            """));
    }

    /// <summary>
    /// 1 本の仕訳を取り消す反対仕訳は 1 本まで。
    /// </summary>
    /// <remarks>
    /// アプリも計上前に検査するが、同時に 2 人が取り消すと両方が「まだ取り消されていない」を
    /// 読んでしまう。**二重取消が通ると残高が原仕訳 1 本分ずれる。**
    /// </remarks>
    [Fact]
    public void 同じ仕訳を二度取り消せない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, Reversal(id: 2, entryNo: 2));

        // 止まるのは 2 本目を**計上する**ところ。下書きのままなら作れる。
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Reversal(id: 3, entryNo: 3)));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM journal_entries WHERE entry_type = 'reversal' AND status = 'posted'"));
    }

    /// <summary>下書きのままなら何本でも作れる（計上した 1 本だけが帳簿に載る）。</summary>
    [Fact]
    public void 取消の下書きは何本でも作れる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, ReversalDraft(2) + ReversalDraft(3));

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries WHERE entry_type = 'reversal'"));
    }

    /// <summary>
    /// 1 本の仕訳を訂正する再計上も 1 本まで（ADR-0015）。
    /// </summary>
    /// <remarks>
    /// <b>再計上が 2 本載ると、直した内容がそのまま二重に計上される。</b>
    /// 取消と同じく、アプリ側の検査は同時に 2 人が訂正した場合に勝てない。
    /// </remarks>
    [Fact]
    public void 同じ仕訳を二度訂正できない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, Amendment(id: 2, entryNo: 2, entryType: "reversal"));
        TestDatabase.Execute(db, Amendment(id: 3, entryNo: 3, entryType: "correction"));

        Assert.Throws<SqliteException>(() =>
            TestDatabase.Execute(db, Amendment(id: 4, entryNo: 4, entryType: "correction")));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM journal_entries WHERE entry_type = 'correction' AND status = 'posted'"));
    }

    /// <summary>
    /// 取消と再計上は別々に数える。同じ原仕訳に 1 本ずつ載るのが訂正の正常な姿である。
    /// </summary>
    [Fact]
    public void 取消と再計上は同じ原仕訳に一本ずつ載せられる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, Amendment(id: 2, entryNo: 2, entryType: "reversal"));
        TestDatabase.Execute(db, Amendment(id: 3, entryNo: 3, entryType: "correction"));

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM journal_entries WHERE original_entry_id = 1 AND status = 'posted'"));
    }

    /// <summary>
    /// 一意なのは「原仕訳ごと」であって「帳簿全体で 1 本」ではない。
    /// </summary>
    /// <remarks>
    /// インデックスの列を <c>(original_entry_id)</c> から <c>(entry_type)</c> に取り違えても、
    /// 原仕訳が 1 本しか無いテストでは緑のままになる。症状は
    /// 「帳簿全体で訂正が 1 本しか計上できない」で、最初の 1 件は通るぶん発見が遅れる。
    /// </remarks>
    [Theory]
    [InlineData("reversal")]
    [InlineData("correction")]
    public void 別々の原仕訳ならそれぞれ足せる(string entryType)
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        // 2 本目の原仕訳を作って計上する。
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, id, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 10, 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (10, 1, 'debit', 1, 500, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 9, posted_at = '2026-05-20 10:00:00' WHERE id = 10;
            """);

        TestDatabase.Execute(db, Amendment(id: 2, entryNo: 2, entryType: entryType));
        TestDatabase.Execute(db, Amendment(id: 3, entryNo: 3, entryType: entryType, originalEntryId: 10));

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db,
            $"SELECT COUNT(*) FROM journal_entries WHERE entry_type = '{entryType}' AND status = 'posted'"));
    }

    private static string ReversalDraft(int id) => AmendmentDraft(id, "reversal");

    private static string Reversal(int id, int entryNo) => Amendment(id, entryNo, "reversal");

    private static string AmendmentDraft(int id, string entryType, int originalEntryId = 1) => $"""
        INSERT INTO journal_entries (description, id, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
            VALUES ('5 月分の現金売上', {id}, 1, '2026-05-20', '2026-05-21', 'draft', '{entryType}', {originalEntryId}, '2026-05-21 10:00:00');
        """;

    private static string Amendment(int id, int entryNo, string entryType, int originalEntryId = 1)
        => $"""
        {AmendmentDraft(id, entryType, originalEntryId)}
        UPDATE journal_entries SET status = 'posted', entry_no = {entryNo}, posted_at = '2026-05-21 10:00:00' WHERE id = {id};
        """;
}
