namespace BusinessApp.Schema.Tests;

using Microsoft.Data.Sqlite;

/// <summary>
/// CHECK・UNIQUE・外部キーによる不変条件の担保（Designer/ddl/README）。
/// </summary>
public class SchemaConstraintTests
{
    private static SqliteConnection Seeded()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        return db;
    }

    [Fact]
    public void 単一法人なので事業所情報は一行しか持てない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            "INSERT INTO company_profile (name, fiscal_year_end_month) VALUES ('二社目', 3);"));
    }

    [Fact]
    public void 全社共通の部門は一件しか持てない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            "INSERT INTO departments (code, name, is_company_wide) VALUES ('D99', 'もう一つの全社共通', 1);"));
    }

    [Fact]
    public void 全社共通でない部門はいくつでも作れる()
    {
        using var db = Seeded();

        TestDatabase.Execute(db, """
            INSERT INTO departments (code, name, is_company_wide) VALUES ('D02', '開発部', 0);
            INSERT INTO departments (code, name, is_company_wide) VALUES ('D03', '管理部', 0);
            """);

        Assert.Equal(4L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM departments"));
    }

    /// <summary>I-06 訂正・取消は原仕訳を一意に特定する情報を持つ。</summary>
    [Theory]
    [InlineData("correction")]
    [InlineData("reversal")]
    public void 訂正と取消は原仕訳なしでは作れない(string entryType)
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, $"""
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', '{entryType}', '2026-05-21 10:00:00');
            """));
    }

    /// <summary>I-17 伝票番号を再利用しない。</summary>
    [Fact]
    public void 同じ会計年度で伝票番号は重複できない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, entry_no, transaction_date, posting_date, status, entry_type, entered_at, posted_at)
                VALUES (1, 1, '2026-05-21', '2026-05-21', 'posted', 'normal', '2026-05-21 10:00:00', '2026-05-21 10:00:00');
            """));
    }

    [Fact]
    public void 下書きに伝票番号を与えられない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, entry_no, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, 99, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            """));
    }

    [Fact]
    public void 計上済みには伝票番号と計上日時が要る()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'posted', 'normal', '2026-05-21 10:00:00');
            """));
    }

    [Fact]
    public void 複数の下書きは伝票番号が空のまま共存できる()
    {
        using var db = Seeded();

        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-22', '2026-05-22', 'draft', 'normal', '2026-05-22 10:00:00');
            """);

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries WHERE entry_no IS NULL"));
    }

    /// <summary>I-14 同一の外部伝票を二重に計上しない。</summary>
    [Fact]
    public void 冪等性キーは重複できない()
    {
        using var db = Seeded();

        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at, idempotency_key)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00', 'EXPENSE-001');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at, idempotency_key)
                VALUES (1, '2026-05-22', '2026-05-22', 'draft', 'normal', '2026-05-22 10:00:00', 'EXPENSE-001');
            """));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 明細の金額は正でなければならない(int amount)
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, $"""
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, {amount}, 1);
            """));
    }

    [Fact]
    public void 消費税行は親行の指定が要る()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, is_tax_line)
                VALUES (1, 1, 'debit', 1, 100, 1, 1);
            """));
    }

    [Fact]
    public void 本体行は親行を持てない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, parent_line_no)
                VALUES (1, 1, 'debit', 1, 100, 1, 2);
            """));
    }

    [Fact]
    public void 存在しない勘定科目は参照できない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 9999, 100, 1);
            """));
    }

    [Theory]
    [InlineData("INSERT INTO accounts (code, name, category) VALUES ('9999', '謎', 'unknown')")]
    [InlineData("INSERT INTO tax_categories (code, name, taxation_type) VALUES ('X', '謎', 'unknown')")]
    [InlineData("INSERT INTO fiscal_years (code, label, start_date, end_date, status) VALUES ('FY19', '第 19 期', '2027-04-01', '2028-03-31', 'unknown')")]
    public void 区分の値は決められたものしか入らない(string sql)
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));
    }

    [Fact]
    public void 会計年度の終期は始期より前にできない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            "INSERT INTO fiscal_years (code, label, start_date, end_date) VALUES ('FY19', '第 19 期', '2028-03-31', '2027-04-01');"));
    }

    [Fact]
    public void 同じ伝票の中で行番号は重複できない()
    {
        using var db = Seeded();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 100, 1);
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'credit', 1, 100, 1);
            """));
    }

    [Fact]
    public void 採番の次番号は一以上でなければならない()
    {
        using var db = Seeded();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db,
            "UPDATE journal_entry_sequences SET next_entry_no = 0 WHERE fiscal_year_id = 1;"));
    }
}
