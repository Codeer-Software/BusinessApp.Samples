namespace BusinessApp.AccountingCore.Tests.Fixtures;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.Partners;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>検証テストの素材。第 18 期（2026-04-01〜2027-03-31）を月次で持つ。</summary>
public static class AccountingFixture
{
    public static readonly FiscalYearId FiscalYear = new(18);
    public static readonly FiscalYearId OtherFiscalYear = new(17);

    // 識別子（DB の主キー）と科目コードは別物である。
    // 前者はシステムが採番し、後者は利用者が見る自然キー。
    public static readonly AccountId Cash = new(1);
    public static readonly AccountId AccountsPayable = new(2);
    public static readonly AccountId Sales = new(3);
    public static readonly AccountId SuppliesExpense = new(4);
    public static readonly AccountId RetiredExpense = new(5);
    public static readonly AccountId BankAccount = new(6);

    /// <summary>補助科目を使うが、<b>選べる補助科目が 1 つも無い</b>科目（無効なものだけがある）。</summary>
    public static readonly AccountId CurrentAccount = new(7);
    public static readonly AccountId UnknownAccount = new(999);

    public static readonly SubAccountId MainBank = new(1);
    public static readonly SubAccountId RetiredBank = new(2);
    public static readonly SubAccountId SubAccountOfCash = new(3);
    public static readonly SubAccountId RetiredCurrent = new(4);
    public static readonly SubAccountId UnknownSubAccount = new(999);

    public static readonly DepartmentId SalesDepartment = new(1);
    public static readonly DepartmentId RetiredDepartment = new(2);
    public static readonly DepartmentId UnknownDepartment = new(999);

    public static readonly PartnerId Partner = new(1);
    public static readonly TaxCategoryId OutOfScope = new(1);
    public static readonly TaxCategoryId TaxablePurchase = new(2);

    public static IReadOnlyList<AccountDefinition> Accounts { get; } =
    [
        new(Cash, "1100", "現金", AccountCategory.Asset),
        new(AccountsPayable, "2100", "買掛金", AccountCategory.Liability),
        new(Sales, "4000", "売上高", AccountCategory.Revenue),
        new(SuppliesExpense, "5200", "消耗品費", AccountCategory.Expense),
        new(RetiredExpense, "5900", "廃止した費用科目", AccountCategory.Expense, IsActive: false),
        new(BankAccount, "1200", "普通預金", AccountCategory.Asset, UsesSubAccount: true),
        new(CurrentAccount, "1210", "当座預金", AccountCategory.Asset, UsesSubAccount: true),
    ];

    public static IReadOnlyList<SubAccountDefinition> SubAccounts { get; } =
    [
        new(MainBank, BankAccount, "01", "みずほ銀行"),
        new(RetiredBank, BankAccount, "99", "解約した口座", IsActive: false),
        new(SubAccountOfCash, Cash, "01", "レジ"),
        new(RetiredCurrent, CurrentAccount, "01", "解約した当座", IsActive: false),
    ];

    public static IReadOnlyList<DepartmentDefinition> Departments { get; } =
    [
        new(SalesDepartment, "20", "営業部"),
        new(RetiredDepartment, "99", "廃止した部門", IsActive: false),
    ];

    public static PostingContext Context(
        PeriodStatus septemberStatus = PeriodStatus.Open,
        PeriodStatus fiscalYearStatus = PeriodStatus.Open)
        => new(new AccountCatalog(Accounts),
               new SubAccountCatalog(SubAccounts),
               new DepartmentCatalog(Departments),
               Calendar(septemberStatus, fiscalYearStatus));

    public static FiscalCalendar Calendar(
        PeriodStatus septemberStatus = PeriodStatus.Open,
        PeriodStatus fiscalYearStatus = PeriodStatus.Open)
    {
        var fiscalYear = new FiscalYear(
            FiscalYear,
            "FY18",
            "第 18 期（2026 年度）",
            new DateRange(new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 31)),
            fiscalYearStatus,
            PremiumLedgerFrom: new DateOnly(2026, 4, 1));

        var periods = Enumerable.Range(0, 12).Select(offset =>
        {
            var start = new DateOnly(2026, 4, 1).AddMonths(offset);
            var end = start.AddMonths(1).AddDays(-1);
            var status = start.Month == 9 ? septemberStatus : PeriodStatus.Open;
            return new AccountingPeriod(new AccountingPeriodId(offset + 1), FiscalYear, new DateRange(start, end), status);
        });

        return new FiscalCalendar([fiscalYear], periods);
    }

    /// <summary>現金売上 1 本。損益科目には部門を付ける（I-13）。</summary>
    public static JournalEntry CashSale(DateOnly date, long amount = 100_000)
        => Entry(date,
            Line(1, DebitCredit.Debit, Cash, amount),
            Line(2, DebitCredit.Credit, Sales, amount, department: SalesDepartment));

    /// <summary>
    /// 伝票 1 本。<b>取引日と計上日は既定でずらす。</b>
    /// </summary>
    /// <remarks>
    /// 同じ値にすると、取引日を書くべき場所に計上日を書いても全テストが緑のままになる
    /// （qa/03 L-02。実際に取消の取引日で踏んだ）。同じ日にしたいテストだけが明示する。
    /// </remarks>
    public static JournalEntry Entry(DateOnly date, params JournalLine[] lines)
        => Entry(date, date.AddDays(2), lines);

    /// <summary>摘要の既定値。<b>計上には要る</b>ので、既定で入れておく（docs/10 §4-2-1）。</summary>
    /// <remarks>
    /// <para><b>空の摘要を試すテストは <c>with { Description = null }</c> と書く。</b>
    /// 既定を空のままにすると、摘要を要求する関門を入れた日に<b>全部のテストが赤になる</b>ので、
    /// 「摘要が要る」ことを検査しているテストと、そうでないテストの区別が付かなくなる。</para>
    /// <para><b>サーバ検体・訂正のテストが使う字とは別にしてある</b>——同じにすると、
    /// 訂正の摘要から接頭辞を剥がす検査が、既定値だけで成立して緑になる（qa/03 L-02 の縮退）。</para>
    /// </remarks>
    public const string DefaultDescription = "5 月分の現金売上";

    public static JournalEntry Entry(DateOnly date, DateOnly postingDate, params JournalLine[] lines)
        => new()
        {
            Id = new JournalEntryId(1),
            FiscalYearId = FiscalYear,
            TransactionDate = date,
            PostingDate = postingDate,
            Status = EntryStatus.Draft,
            EntryType = EntryType.Normal,
            Description = DefaultDescription,
            EnteredAt = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.FromHours(9)),
            Lines = lines,
        };

    public static JournalLine Line(
        int lineNo,
        DebitCredit side,
        AccountId accountId,
        long amount,
        DepartmentId? department = null,
        TaxCategoryId? taxCategoryId = null,
        SubAccountId? subAccountId = null)
        => new()
        {
            LineNo = lineNo,
            DebitCredit = side,
            AccountId = accountId,
            SubAccountId = subAccountId,
            DepartmentId = department,
            Amount = Yen.From(amount),
            TaxCategoryId = taxCategoryId ?? OutOfScope,
        };
}
