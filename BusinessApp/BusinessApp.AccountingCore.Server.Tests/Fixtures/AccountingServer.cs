namespace BusinessApp.AccountingCore.Server.Tests.Fixtures;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;
using System.Globalization;

/// <summary>
/// 本物の DDL と初期データを載せた SQLite の上に、サーバ側部品を組み立てて渡す。
/// </summary>
/// <remarks>
/// <b>組み立て方も検証の対象である。</b> テストごとに部品を手で繋ぐと、
/// 本番の組み立て（<c>CustomizedModuleDataIO</c>）とずれても気づけない。
/// </remarks>
internal sealed class AccountingServer : IDisposable
{
    /// <summary>計上の時刻。テストで日付をまたぐ話をしないので固定する。</summary>
    public static readonly DateTimeOffset Now = new(2026, 8, 24, 13, 6, 46, TimeSpan.FromHours(9));

    /// <summary>初期データの第 18 期（2026-04-01 〜 2027-03-31）。</summary>
    public static readonly FiscalYearId FiscalYear = new(1);

    private readonly SqliteConnection connection;

    public AccountingServer()
    {
        connection = TestDatabase.CreateWithSeed();
        var accessor = new SqliteDbAccessor(connection);

        MasterLoader = new AccountingMasterLoader(accessor, SqliteDbAccessor.DataSourceName);
        EntryStore = new JournalEntryStore(accessor, SqliteDbAccessor.DataSourceName);
        SequenceStore = new EntryNumberSequenceStore(accessor, SqliteDbAccessor.DataSourceName);
        Gate = new JournalSubmitGate(MasterLoader, EntryStore, SequenceStore, new FixedTimeProvider(Now));
    }

    public AccountingMasterLoader MasterLoader { get; }

    public JournalEntryStore EntryStore { get; }

    public EntryNumberSequenceStore SequenceStore { get; }

    public JournalSubmitGate Gate { get; }

    /// <summary>下書きの伝票を 1 件入れて、その識別子を返す。</summary>
    public JournalEntryId InsertDraft(
        string transactionDate = "2026-08-24",
        string postingDate = "2026-08-24",
        string entryType = "normal",
        JournalEntryId? originalEntryId = null)
    {
        // 訂正・取消は原仕訳が要る（I-06）。DDL の CHECK は INSERT の時点で効くので、
        // 後から UPDATE で足すことはできない。
        var original = originalEntryId is { } value ? value.Value.ToString(CultureInfo.InvariantCulture) : "null";

        Execute($"""
            insert into journal_entries
                (fiscal_year_id, transaction_date, posting_date, status, entry_type,
                 original_entry_id, entered_at)
            values ({FiscalYear.Value}, '{transactionDate}', '{postingDate}', 'draft', '{entryType}',
                    {original}, '2026-08-24 13:00:00')
            """);

        return new JournalEntryId(Scalar<long>("select last_insert_rowid()"));
    }

    /// <summary>明細を 1 行足す。勘定科目と税区分はコードで引く（初期データの id を書き写さない）。</summary>
    public void InsertLine(
        JournalEntryId entryId,
        int lineNo,
        string debitCredit,
        string accountCode,
        long amount,
        string taxCategoryCode = "OUT",
        string? departmentCode = null)
    {
        var department = departmentCode is null
            ? "null"
            : $"(select id from departments where code = '{departmentCode}')";

        Execute($"""
            insert into journal_lines
                (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, department_id)
            values ({entryId.Value}, {lineNo}, '{debitCredit}',
                    (select id from accounts where code = '{accountCode}'),
                    {amount},
                    (select id from tax_categories where code = '{taxCategoryCode}'),
                    {department})
            """);
    }

    /// <summary>取引先を 1 件足す（初期データには 0 件しか無い）。</summary>
    public long InsertPartner(string code = "P001", string name = "株式会社れい")
    {
        Execute($"insert into partners (code, name) values ('{code}', '{name}')");
        return Scalar<long>("select last_insert_rowid()");
    }

    /// <summary>補助科目を 1 件足す。勘定科目はコードで引く。</summary>
    public long InsertSubAccount(string accountCode, string code = "S001", string name = "本店")
    {
        Execute($"""
            insert into sub_accounts (account_id, code, name)
            values ((select id from accounts where code = '{accountCode}'), '{code}', '{name}')
            """);
        return Scalar<long>("select last_insert_rowid()");
    }

    /// <summary>コードから識別子を引く（初期データの id をテストに書き写さない）。</summary>
    public AccountId AccountOf(string code)
        => new(Scalar<long>($"select id from accounts where code = '{code}'"));

    public DepartmentId DepartmentOf(string code)
        => new(Scalar<long>($"select id from departments where code = '{code}'"));

    public TaxCategoryId TaxCategoryOf(string code)
        => new(Scalar<long>($"select id from tax_categories where code = '{code}'"));

    public void Execute(string sql) => TestDatabase.Execute(connection, sql);

    public T Scalar<T>(string sql) => TestDatabase.ScalarOf<T>(connection, sql);

    /// <summary>NULL のときに既定値を返す照会（<c>entry_no</c> が未採番のときなど）。</summary>
    public string TextOrEmpty(string sql) => TestDatabase.Query(connection, sql).FirstOrDefault() ?? string.Empty;

    public void Dispose() => connection.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
