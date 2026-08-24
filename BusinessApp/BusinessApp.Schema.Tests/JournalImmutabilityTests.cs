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
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
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

    /// <summary>訂正・取消は反対仕訳で行う。原仕訳は残したまま新しい伝票が増える。</summary>
    [Fact]
    public void 取消は反対仕訳として別の伝票になる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'reversal', 1, '2026-05-21 10:00:00');
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
            INSERT INTO journal_entries (fiscal_year_id, entry_no, transaction_date, posting_date, status, entry_type, entered_at, posted_at)
                VALUES (1, 99, '2026-05-20', '2026-05-20', 'posted', 'normal', '2026-05-20 10:00:00', '2026-05-20 10:00:00');
            """));

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries"));
    }

    /// <summary>
    /// 金額は整数円でなければならない（docs/04 §3）。
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
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
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

    private static string ReversalDraft(int id) => $"""
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
            VALUES ({id}, 1, '2026-05-20', '2026-05-21', 'draft', 'reversal', 1, '2026-05-21 10:00:00');
        """;

    private static string Reversal(int id, int entryNo) => ReversalDraft(id) + $"""
        UPDATE journal_entries SET status = 'posted', entry_no = {entryNo}, posted_at = '2026-05-21 10:00:00' WHERE id = {id};
        """;
}
