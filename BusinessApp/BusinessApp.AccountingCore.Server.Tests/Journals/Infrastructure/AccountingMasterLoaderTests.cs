namespace BusinessApp.AccountingCore.Server.Tests.Journals.Infrastructure;

using System.Text.Json;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.Partners;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals.Infrastructure;

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
    /// <summary>
    /// <b>伝票が参照している取引先だけ</b>を読んで目録に足す。名前と有効を読み戻せる。
    /// </summary>
    /// <remarks>
    /// 参照していない取引先は目録に入らない（全件は読まない。<see cref="PartnerCatalog"/>）——
    /// 検体に 3 件置いて、伝票の取引先と明細の取引先の 2 件だけが入ることを見る。
    /// </remarks>
    [Fact]
    public async Task 参照している取引先だけを目録に足す()
    {
        using var server = new AccountingServer();
        var onEntry = server.InsertPartner("P001", "伝票の取引先");
        var onLine = server.InsertPartner("P002", "明細の取引先");
        var unrelated = server.InsertPartner("P003", "関係ない取引先");
        server.Execute($"update partners set is_active = 0 where id = {onLine}");
        var id = server.InsertDraft();
        server.Execute($"update journal_entries set partner_id = {onEntry} where id = {id.Value}");
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "1110", 1000);
        server.Execute($"update journal_lines set partner_id = {onLine} where journal_entry_id = {id.Value} and line_no = 2");
        var draft = await server.EntryStore.LoadAsync(id);

        var context = await server.MasterLoader.WithPartnersAsync(await server.MasterLoader.LoadAsync(), draft);

        Assert.Equal("伝票の取引先", context.Partners.Find(new PartnerId(onEntry))!.Name);
        Assert.True(context.Partners.Find(new PartnerId(onEntry))!.IsActive);
        Assert.False(context.Partners.Find(new PartnerId(onLine))!.IsActive);
        Assert.Null(context.Partners.Find(new PartnerId(unrelated)));
        Assert.True(context.HasSelectablePartner);
        Assert.True(context.Partners.IsLoaded);
    }

    /// <summary>
    /// <b>読む前の目録では取引先を引けない</b>（<see cref="PartnerCatalog.Unloaded"/>）——
    /// 空の目録で計上検証を通すと、参照している取引先が全部「マスタに無い」になる。
    /// </summary>
    [Fact]
    public async Task 読む前の目録では取引先を引けない()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner();

        var context = await server.MasterLoader.LoadAsync();

        Assert.False(context.Partners.IsLoaded);
        Assert.True(context.HasSelectablePartner);
        Assert.Throws<InvalidOperationException>(() => context.Partners.Find(new PartnerId(partner)));
    }

    /// <summary>取引先を 1 つも参照していなければ、DB を読まずに「読んだ」空の目録にする。</summary>
    [Fact]
    public async Task 取引先を参照していなければ読まずに空の目録にする()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "1110", 1000);
        var before = await server.MasterLoader.LoadAsync();
        server.FailBeforeStatement = sql => sql.Contains("from partners", StringComparison.Ordinal)
            ? new InvalidOperationException("参照が無いのに取引先を読みに行った")
            : null;

        var after = await server.MasterLoader.WithPartnersAsync(before, await server.EntryStore.LoadAsync(id));

        Assert.True(after.Partners.IsLoaded);
        Assert.Null(after.Partners.Find(new PartnerId(partner)));
        Assert.True(after.HasSelectablePartner);
    }

    [Fact]
    public async Task 目録か伝票を渡さなければ止まる()
    {
        using var server = new AccountingServer();
        var context = await server.MasterLoader.LoadAsync();
        var draft = await server.EntryStore.LoadAsync(server.InsertDraft());

        await Assert.ThrowsAsync<ArgumentNullException>(() => server.MasterLoader.WithPartnersAsync(null!, draft));
        await Assert.ThrowsAsync<ArgumentNullException>(() => server.MasterLoader.WithPartnersAsync(context, null!));
    }

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
        Assert.False(cash.UsesSubAccount);
        Assert.False(cash.IsContra);
        Assert.True(cash.IsActive);

        // 預金は補助科目（口座）を持つ。評価勘定は残高が増える側が科目区分と逆になる。
        Assert.True(deposit.UsesSubAccount);
        Assert.True(allowance.IsContra);
        Assert.Equal(DebitCredit.Credit, allowance.NormalBalance);

        // 取引先を要するのは相手方別の帳簿が要る科目だけである（docs/10 §6-2）。
        Assert.False(cash.RequiresPartner);
        Assert.True(context.Accounts.Find(server.AccountOf("1300"))!.RequiresPartner);
    }

    [Fact]
    public async Task 選べる取引先の有無を読む()
    {
        // **「選んでください」と言ってよいか**の判定に使う（docs/21 §2-3）。
        // 一覧ではなく有無だけを持つ理由は PostingContext の注記。
        using var server = new AccountingServer();
        Assert.False((await server.MasterLoader.LoadAsync()).HasSelectablePartner);

        var partner = server.InsertPartner();
        Assert.True((await server.MasterLoader.LoadAsync()).HasSelectablePartner);

        // **無効な取引先は「選べる」に数えない。** 明細の候補ダイアログが is_active で絞っているので、
        // ここを揃えないと 0 件のダイアログに向けて「選んでください」と言うことになる。
        server.Execute($"update partners set is_active = 0 where id = {partner}");
        Assert.False((await server.MasterLoader.LoadAsync()).HasSelectablePartner);
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

    /// <summary>
    /// <b>「選べる」の広さが、画面の候補の絞りと同じであること。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>已むを得ない重複はデザイン JSON と突き合わせる</b>（docs/20 §4。
    /// <c>MasterMeaningGateTests</c> の列の突き合わせと同じ作法）。
    /// <c>AccountingMasterLoader</c> は <c>is_active = 1</c> で数え、画面は
    /// <c>SearchCondition</c> で候補を絞る——**片方に条件が増えた日**に、
    /// <b>候補が 0 件で開くのに「選んでください」と言う</b>状態になる（それを避けるための分岐なのに）。</para>
    /// <para><b>取引先の欄は伝票と明細の 2 か所にある。</b> どちらも同じ 1 条件であることを見る。</para>
    /// </remarks>
    [Theory]
    [InlineData("JournalEntry")]
    [InlineData("JournalLine")]
    public void 取引先の候補の絞りは有効だけである(string module)
    {
        var path = Directory
            .EnumerateFiles(TestSupport.TestDatabase.ModulesDirectory, $"{module}.mod.json", SearchOption.AllDirectories)
            .Single();
        using var design = JsonDocument.Parse(File.ReadAllText(path));

        var partner = design.RootElement.GetProperty("Fields").EnumerateArray()
            .Single(f => f.GetProperty("Name").GetString() == "Partner");
        var children = partner.GetProperty("SearchCondition").GetProperty("Condition").GetProperty("Children");

        var only = Assert.Single(children.EnumerateArray());
        Assert.Equal("IsActive.Value", only.GetProperty("SearchTargetVariable").GetString());
        Assert.Equal("Equal", only.GetProperty("Comparison").GetString());
        Assert.True(only.GetProperty("Value").GetProperty("Value").GetBoolean());
    }
}
