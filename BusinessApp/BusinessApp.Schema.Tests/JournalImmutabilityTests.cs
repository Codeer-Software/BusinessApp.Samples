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
}
