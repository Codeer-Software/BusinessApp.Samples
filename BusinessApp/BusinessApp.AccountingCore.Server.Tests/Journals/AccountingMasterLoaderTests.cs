namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上検証に要るマスタの読み込み。
/// </summary>
/// <remarks>
/// 初期データ（<c>Designer/seed/</c>）をそのまま読む。
/// <b>件数を書き写して固定しない。</b> 科目が 1 つ増えるたびにテストが赤くなるのは検査ではなく足枷である。
/// 見るのは「意味のある値が正しく変換されているか」だけにする。
/// </remarks>
public class AccountingMasterLoaderTests
{
    [Fact]
    public async Task 勘定科目は区分と属性まで読む()
    {
        using var server = new AccountingServer();

        var context = await server.MasterLoader.LoadAsync();
        var cash = context.Accounts.Find(server.AccountOf("1100"))!;
        var deposit = context.Accounts.Find(server.AccountOf("1200"))!;
        var allowance = context.Accounts.Find(server.AccountOf("1350"))!;

        Assert.Equal("現金", cash.Name);
        Assert.Equal("1100", cash.Code);
        Assert.Equal(AccountCategory.Asset, cash.Category);
        Assert.False(cash.RequiresSubAccount);
        Assert.False(cash.IsContra);
        Assert.True(cash.IsActive);

        // 預金は補助科目（口座）を持つ。評価勘定は残高が増える側が科目区分と逆になる。
        Assert.True(deposit.RequiresSubAccount);
        Assert.True(allowance.IsContra);
        Assert.Equal(DebitCredit.Credit, allowance.NormalBalance);
    }

    [Fact]
    public async Task 無効にした勘定科目も読む_選ばせないのは検証の仕事()
    {
        using var server = new AccountingServer();
        server.Execute("update accounts set is_active = 0 where code = '1100'");

        var context = await server.MasterLoader.LoadAsync();

        Assert.False(context.Accounts.Find(server.AccountOf("1100"))!.IsActive);
    }

    [Fact]
    public async Task 既定税区分は未設定なら_null_のまま()
    {
        using var server = new AccountingServer();
        server.Execute("update accounts set default_tax_category_id = null where code = '1100'");
        server.Execute(
            "update accounts set default_tax_category_id = (select id from tax_categories where code = 'TP') where code = '6110'");

        var context = await server.MasterLoader.LoadAsync();

        Assert.Null(context.Accounts.Find(server.AccountOf("1100"))!.DefaultTaxCategoryId);
        Assert.Equal(server.TaxCategoryOf("TP"), context.Accounts.Find(server.AccountOf("6110"))!.DefaultTaxCategoryId);
    }

    [Fact]
    public async Task 補助科目は所属する勘定科目とともに読む()
    {
        using var server = new AccountingServer();
        var id = new SubAccountId(server.InsertSubAccount("1200", code: "B01", name: "みずほ銀行"));

        var context = await server.MasterLoader.LoadAsync();
        var subAccount = context.SubAccounts.Find(id)!;

        Assert.Equal("B01", subAccount.Code);
        Assert.Equal("みずほ銀行", subAccount.Name);
        Assert.Equal(server.AccountOf("1200"), subAccount.AccountId);
        Assert.True(subAccount.IsActive);
    }

    [Fact]
    public async Task 部門は全社共通かどうかまで読む()
    {
        using var server = new AccountingServer();

        var context = await server.MasterLoader.LoadAsync();
        var companyWide = context.Departments.Find(server.DepartmentOf("00"))!;
        var sales = context.Departments.Find(server.DepartmentOf("20"))!;

        Assert.True(companyWide.IsCompanyWide);
        Assert.Equal("20", sales.Code);
        Assert.Equal("営業部", sales.Name);
        Assert.False(sales.IsCompanyWide);
        Assert.True(sales.IsActive);
    }

    [Fact]
    public async Task 会計年度と会計期間は締めの状態まで読む()
    {
        using var server = new AccountingServer();

        var context = await server.MasterLoader.LoadAsync();
        var year = context.Calendar.FindFiscalYear(AccountingServer.FiscalYear)!;

        Assert.Equal("FY18", year.Code);
        Assert.Equal("第 18 期（2026 年度）", year.Label);
        Assert.Equal(new DateOnly(2026, 4, 1), year.Period.From);
        Assert.Equal(new DateOnly(2027, 3, 31), year.Period.To);
        Assert.Equal(PeriodStatus.Open, year.Status);
        Assert.Equal(new DateOnly(2026, 4, 1), year.PremiumLedgerFrom);

        var period = context.Calendar.ResolvePeriod(new DateOnly(2026, 8, 24))!;
        Assert.Equal(AccountingServer.FiscalYear, period.FiscalYearId);
        Assert.Equal(new DateOnly(2026, 8, 1), period.Period.From);
        Assert.Equal(PeriodStatus.Open, period.Status);
        Assert.True(context.Calendar.IsPostable(new DateOnly(2026, 8, 24)));
    }

    [Fact]
    public async Task 優良な電子帳簿の適用開始日は未設定なら_null_のまま()
    {
        using var server = new AccountingServer();
        server.Execute("update fiscal_years set premium_ledger_from = null");

        var context = await server.MasterLoader.LoadAsync();

        Assert.Null(context.Calendar.FindFiscalYear(AccountingServer.FiscalYear)!.PremiumLedgerFrom);
    }

    [Fact]
    public async Task 締めた会計期間は締めの状態として読む()
    {
        using var server = new AccountingServer();
        server.Execute("update accounting_periods set status = 'closed' where start_date = '2026-08-01'");

        var context = await server.MasterLoader.LoadAsync();

        Assert.Equal(PeriodStatus.Closed, context.Calendar.ResolvePeriod(new DateOnly(2026, 8, 24))!.Status);
        Assert.False(context.Calendar.IsPostable(new DateOnly(2026, 8, 24)));
    }
}
