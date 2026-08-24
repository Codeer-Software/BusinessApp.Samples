namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>制約の検査に要る最小限のデータ。会計年度 1 本・月次期間 1 本・マスタ数件。</summary>
internal static class SchemaSeed
{
    public const string Masters = """
        INSERT INTO company_profile (name, fiscal_year_end_month) VALUES ('株式会社テスト', 3);
        INSERT INTO fiscal_years (code, label, start_date, end_date, status, premium_ledger_from)
            VALUES ('FY18', '第 18 期（2026 年度）', '2026-04-01', '2027-03-31', 'open', '2026-04-01');
        INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date, status)
            VALUES (1, '2026-05-01', '2026-05-31', 'open');
        INSERT INTO tax_categories (code, name, taxation_type) VALUES ('OUT', '対象外', 'out_of_scope');
        INSERT INTO accounts (code, name, category) VALUES ('1100', '現金', 'asset');
        INSERT INTO accounts (code, name, category) VALUES ('4000', '売上高', 'revenue');
        INSERT INTO departments (code, name, is_company_wide) VALUES ('D00', '全社共通', 1);
        INSERT INTO departments (code, name, is_company_wide) VALUES ('D01', '営業部', 0);
        INSERT INTO partners (code, name) VALUES ('P001', '株式会社取引先');
        INSERT INTO journal_entry_sequences (fiscal_year_id) VALUES (1);
        """;

    /// <summary>
    /// 下書きを作り、明細を入れ、計上する。<b>これが唯一の正しい経路</b>であり、
    /// 計上済みに明細を足す経路はトリガが塞いでいる。
    /// </summary>
    public const string PostedEntry = """
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 1, 'debit', 1, 100000, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
            VALUES (1, 2, 'credit', 2, 2, 100000, 1);
        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00' WHERE id = 1;
        UPDATE journal_entry_sequences SET next_entry_no = next_entry_no + 1 WHERE fiscal_year_id = 1;
        """;

    /// <summary>マスタと計上済み仕訳 1 本を入れた DB を返す。</summary>
    public static SqliteConnection CreateWithPostedEntry()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, Masters);
        TestDatabase.Execute(db, PostedEntry);
        return db;
    }
}
