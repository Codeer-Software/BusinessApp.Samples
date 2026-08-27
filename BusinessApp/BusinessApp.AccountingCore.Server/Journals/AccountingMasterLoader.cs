namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.ServerSupport;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 計上検証に要るマスタを DB から読む。
/// </summary>
/// <remarks>
/// <para>ここが <b>long ↔ 型付き識別子の変換を閉じ込める境界</b>である（ADR-0014）。
/// ドメインの内側に生の <c>long</c> を持ち込まない。</para>
/// <para>会計マスタは全件を先に読む。科目 100 件・部門数件・税区分 10 件の規模なので、
/// 明細ごとに引き直すより 1 回読む方が速く、検証が純粋関数のままでいられる。</para>
/// </remarks>
public sealed class AccountingMasterLoader(IDbAccessor dbAccessor, string dataSourceName)
{
    public async Task<PostingContext> LoadAsync()
        => new(await LoadAccountsAsync(),
               await LoadSubAccountsAsync(),
               await LoadDepartmentsAsync(),
               await LoadCalendarAsync());

    private async Task<AccountCatalog> LoadAccountsAsync()
    {
        var rows = await QueryAsync(
            "select id, code, name, category, default_tax_category_id, requires_sub_account, is_contra, is_active from accounts");

        return new AccountCatalog(rows.Select(r => new AccountDefinition(
            new AccountId(DbValue.ToLong(r["id"])),
            DbValue.ToText(r["code"]),
            DbValue.ToText(r["name"]),
            DbValue.ToEnum<AccountCategory>(r["category"]),
            DbValue.IsNull(r["default_tax_category_id"]) ? null : new TaxCategoryId(DbValue.ToLong(r["default_tax_category_id"])),
            DbValue.ToBool(r["requires_sub_account"]),
            DbValue.ToBool(r["is_contra"]),
            DbValue.ToBool(r["is_active"]))));
    }

    private async Task<SubAccountCatalog> LoadSubAccountsAsync()
    {
        var rows = await QueryAsync("select id, account_id, code, name, is_active from sub_accounts");

        return new SubAccountCatalog(rows.Select(r => new SubAccountDefinition(
            new SubAccountId(DbValue.ToLong(r["id"])),
            new AccountId(DbValue.ToLong(r["account_id"])),
            DbValue.ToText(r["code"]),
            DbValue.ToText(r["name"]),
            DbValue.ToBool(r["is_active"]))));
    }

    private async Task<DepartmentCatalog> LoadDepartmentsAsync()
    {
        var rows = await QueryAsync("select id, code, name, is_company_wide, is_active from departments");

        return new DepartmentCatalog(rows.Select(r => new DepartmentDefinition(
            new DepartmentId(DbValue.ToLong(r["id"])),
            DbValue.ToText(r["code"]),
            DbValue.ToText(r["name"]),
            DbValue.ToBool(r["is_company_wide"]),
            DbValue.ToBool(r["is_active"]))));
    }

    private async Task<FiscalCalendar> LoadCalendarAsync()
    {
        var yearRows = await QueryAsync(
            "select id, code, label, start_date, end_date, status, premium_ledger_from from fiscal_years");
        var periodRows = await QueryAsync(
            "select id, fiscal_year_id, start_date, end_date, status from accounting_periods");

        var years = yearRows.Select(r => new FiscalYear(
            new FiscalYearId(DbValue.ToLong(r["id"])),
            DbValue.ToText(r["code"]),
            DbValue.ToText(r["label"]),
            new DateRange(DbValue.ToDate(r["start_date"]), DbValue.ToDate(r["end_date"])),
            DbValue.ToEnum<PeriodStatus>(r["status"]),
            DbValue.IsNull(r["premium_ledger_from"]) ? null : DbValue.ToDate(r["premium_ledger_from"])));

        var periods = periodRows.Select(r => new AccountingPeriod(
            new AccountingPeriodId(DbValue.ToLong(r["id"])),
            new FiscalYearId(DbValue.ToLong(r["fiscal_year_id"])),
            new DateRange(DbValue.ToDate(r["start_date"]), DbValue.ToDate(r["end_date"])),
            DbValue.ToEnum<PeriodStatus>(r["status"])));

        return new FiscalCalendar(years, periods);
    }

    private async Task<IReadOnlyList<IDictionary<string, object>>> QueryAsync(string sql)
        => await dbAccessor.QueryAsync(dataSourceName, sql, []);
}
