namespace BusinessApp.AccountingCore.Server.Journals.Infrastructure;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.Partners;
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
               await LoadCalendarAsync(),
               new PartnerCatalog([], await HasSelectablePartnerAsync()));

    /// <summary>
    /// 伝票が参照している取引先だけを読んで、目録に足す。
    /// </summary>
    /// <remarks>
    /// <para><b>全件は読まない</b>（<see cref="PartnerCatalog"/>）。伝票の取引先と明細の取引先の識別子を集め、その分だけ引く。</para>
    /// <para><b>計上の直前に <c>JournalPoster</c> が呼ぶ</b>——検証の経路は複数（画面の保存・取消・訂正の再計上）あるので、
    /// 呼び出し側ごとに足すと 1 経路で忘れる（qa/03 L-14 の型）。</para>
    /// </remarks>
    public async Task<PostingContext> WithPartnersAsync(PostingContext context, JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entry);

        var ids = entry.Lines.Select(l => l.PartnerId)
            .Append(entry.PartnerId)
            .OfType<PartnerId>()
            .Select(p => p.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return context;
        }

        // **識別子は数値で、この場で組み立てた列挙である**（利用者の入力ではない）——それでも文に埋め込まず、パラメータで渡す。
        var parameters = new Dictionary<string, ParamAndRawDbTypeName>();
        var placeholders = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            placeholders.Add($"@p{i}");
            parameters[$"@p{i}"] = new ParamAndRawDbTypeName { Value = ids[i] };
        }

        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            $"select id, name, is_active from partners where id in ({string.Join(", ", placeholders)})",
            parameters);

        var partners = rows.Select(r => new PartnerDefinition(
            new PartnerId(DbValue.ToLong(r["id"])),
            DbValue.ToText(r["name"]),
            DbValue.ToBool(r["is_active"])));

        return context with { Partners = new PartnerCatalog(partners, context.Partners.HasSelectable) };
    }

    private async Task<AccountCatalog> LoadAccountsAsync()
    {
        var rows = await QueryAsync(
            "select id, code, name, category, default_tax_category_id, uses_sub_account, requires_partner, "
            + "is_contra, is_active from accounts");

        return new AccountCatalog(rows.Select(r => new AccountDefinition(
            new AccountId(DbValue.ToLong(r["id"])),
            DbValue.ToText(r["code"]),
            DbValue.ToText(r["name"]),
            DbValue.ToEnum<AccountCategory>(r["category"]),
            DbValue.IsNull(r["default_tax_category_id"]) ? null : new TaxCategoryId(DbValue.ToLong(r["default_tax_category_id"])),
            DbValue.ToBool(r["uses_sub_account"]),
            DbValue.ToBool(r["requires_partner"]),
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

    /// <summary>
    /// 選べる（有効な）取引先が 1 件でもあるか。
    /// </summary>
    /// <remarks>
    /// <b>取引先だけは全件を読まない</b>——数千件になりうるからである（<see cref="PostingContext"/>）。
    /// <b>「有効」の広さは明細の候補ダイアログに揃えてある</b>（JournalLine の Partner フィールドの
    /// 検索条件が <c>IsActive = true</c>）——揃っていないと、
    /// <b>候補が 0 件のまま「選んでください」と言う</b>ことになる。
    /// </remarks>
    private async Task<bool> HasSelectablePartnerAsync()
    {
        var rows = await QueryAsync("select 1 from partners where is_active = 1 limit 1");
        return rows.Count > 0;
    }

    private async Task<IReadOnlyList<IDictionary<string, object>>> QueryAsync(string sql)
        => await dbAccessor.QueryAsync(dataSourceName, sql, []);
}
