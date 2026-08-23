namespace BusinessApp.AccountingCore.Tests.Fixtures;

using BusinessApp.AccountingCore.Calendar;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Masters;
using BusinessApp.AccountingCore.Primitives;
using BusinessApp.AccountingCore.Validation;

/// <summary>検証テストの素材。第 18 期（2026-04-01〜2027-03-31）を月次で持つ。</summary>
public static class AccountingFixture
{
    public const string FiscalYearId = "FY18";
    public const string Cash = "1100";
    public const string AccountsPayable = "2100";
    public const string Sales = "4000";
    public const string SuppliesExpense = "5200";
    public const string RetiredExpense = "5900";
    public const string SalesDepartment = "D01";
    public const string TaxCategoryOutOfScope = "TC-OUT";

    public static IReadOnlyList<AccountDefinition> Accounts { get; } =
    [
        new(Cash, "1100", "現金", AccountCategory.Asset),
        new(AccountsPayable, "2100", "買掛金", AccountCategory.Liability),
        new(Sales, "4000", "売上高", AccountCategory.Revenue),
        new(SuppliesExpense, "5200", "消耗品費", AccountCategory.Expense),
        new(RetiredExpense, "5900", "廃止した費用科目", AccountCategory.Expense, IsActive: false),
    ];

    public static PostingContext Context(PeriodStatus septemberStatus = PeriodStatus.Open,
        PeriodStatus fiscalYearStatus = PeriodStatus.Open)
        => new(new AccountCatalog(Accounts), Calendar(septemberStatus, fiscalYearStatus));

    public static FiscalCalendar Calendar(PeriodStatus septemberStatus = PeriodStatus.Open,
        PeriodStatus fiscalYearStatus = PeriodStatus.Open)
    {
        var fiscalYear = new FiscalYear(
            FiscalYearId,
            "第 18 期（2026 年度）",
            new EffectivePeriod(new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 31)),
            fiscalYearStatus,
            PremiumLedgerFrom: new DateOnly(2026, 4, 1));

        var periods = Enumerable.Range(0, 12).Select(offset =>
        {
            var start = new DateOnly(2026, 4, 1).AddMonths(offset);
            var end = start.AddMonths(1).AddDays(-1);
            var status = start.Month == 9 ? septemberStatus : PeriodStatus.Open;
            return new AccountingPeriod($"{FiscalYearId}-{start:yyyyMM}", FiscalYearId, new EffectivePeriod(start, end), status);
        });

        return new FiscalCalendar([fiscalYear], periods);
    }

    /// <summary>現金売上 1 本。損益科目には部門を付ける（I-13）。</summary>
    public static JournalEntry CashSale(DateOnly date, long amount = 100_000)
        => Entry(date,
            Line(1, DebitCredit.Debit, Cash, amount),
            Line(2, DebitCredit.Credit, Sales, amount, department: SalesDepartment));

    public static JournalEntry Entry(DateOnly date, params JournalLine[] lines)
        => new()
        {
            Id = "JE-TEST",
            TransactionDate = date,
            PostingDate = date,
            Status = EntryStatus.Draft,
            EntryType = EntryType.Normal,
            EnteredAt = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.FromHours(9)),
            Lines = lines,
        };

    public static JournalLine Line(
        int lineNo,
        DebitCredit side,
        string accountId,
        long amount,
        string? department = null,
        string taxCategoryId = TaxCategoryOutOfScope)
        => new()
        {
            LineNo = lineNo,
            DebitCredit = side,
            AccountId = accountId,
            DepartmentId = department,
            Amount = Yen.From(amount),
            TaxCategoryId = taxCategoryId,
        };
}
