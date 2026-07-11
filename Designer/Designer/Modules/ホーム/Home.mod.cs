// Home.mod.cs — ホーム（ダッシュボード）
// 挨拶・やることサマリ（全ロール）＋ 経営サマリ・未処理件数・資金繰りアラート（経理のみ）。
// 経理向けの数値は accounting ゲート付きモジュールを ModuleSearcher で読む
// （画面に直接埋め込まない＝一般ユーザーのホームで権限エラーを出さないため）。
// リスクの高い読み取り（QueryField モジュールの検索）は最後に置く（失敗しても上部は表示済み）。

void Detail_OnAfterInit()
{
    var name = "";
    if (CurrentUser != null && CurrentUser.表示名.Value != null)
    {
        name = CurrentUser.表示名.Value;
    }
    GreetingLabel.Text = $"こんにちは、{name} さん";
    DateLabel.Text = $"{DateTime.Today:yyyy年M月d日}";

    // やることサマリ（全ロール）
    // 承認待ち件数は approval_inbox_view ベースの ApprovalInbox で数える
    // （ADR-0016: フローの CurrentApprover は代表1名のため、並列承認の2人目に件数が出ない）。
    // ApprovalInbox は UserReadCondition（approver/accounting）付きのため、権限のないロールでは検索しない
    // （一般社員の承認待ちは常に0件。権限外モジュールへの検索でホーム初期化が途中停止するのを防ぐ）
    var myApprovals = 0;
    var roleForInbox = CurrentUser.Role.Value;
    if (roleForInbox == "approver" || roleForInbox == "accounting" || roleForInbox == "sysadmin")
    {
        var afs = new ModuleSearcher<ApprovalInbox>();
        afs.AddEquals(f => f.CurrentApprover.Value, CurrentUser.Id.Value);
        myApprovals = afs.Execute().Count;
    }
    else
    {
        // 承認待ち受信箱は承認者・経理・sysadmin 専用（一般社員にはショートカットも出さない）
        GoInbox.IsVisible = false;
    }
    var ers = new ModuleSearcher<ExpenseRequest>();
    ers.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    ers.AddEquals(e => e.SettlementStatus.Value, "applying");
    var myApplying = ers.Execute().Count;
    TodoLabel.Text = $"あなたの承認待ち: {myApprovals} 件 ／ 進行中のあなたの申請: {myApplying} 件";

    // 経理以外はここまで（経理セクションと経理専用ページへのショートカットは非表示）
    if (CurrentUser.Role.Value != "accounting" && CurrentUser.Role.Value != "sysadmin")
    {
        KpiHeadLabel.IsVisible = false;
        KpiLine1.IsVisible = false;
        KpiLine2.IsVisible = false;
        OpsLabel.IsVisible = false;
        CashAlertLabel.IsVisible = false;
        GoJournal.IsVisible = false;
        GoBankImport.IsVisible = false;
        GoMonthlyTrend.IsVisible = false;
        GoCashFlow.IsVisible = false;
        return;
    }

    // 経理: 未処理の件数
    var bls = new ModuleSearcher<BankStatementLine>();
    bls.AddEquals(b => b.Status.Value, "pending");
    var pendingBank = bls.Execute().Count;

    var vis = new ModuleSearcher<VendorInvoice>();
    vis.AddIn(v => v.Status.Value, "received", "accrued");
    var vendorOpen = vis.Execute();
    var dueSoon = 0;
    var dueLimit = DateOnly.FromDateTime(DateTime.Today.AddDays(7));
    foreach (var vm in vendorOpen)
    {
        var v = (VendorInvoice)vm;
        if (v.DueDate.Value == null) continue;
        if (v.DueDate.Value <= dueLimit)
        {
            dueSoon = dueSoon + 1;
        }
    }

    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.Status.Value, "draft");
    var drafts = js.Execute().Count;

    OpsLabel.Text = $"未処理: 銀行明細（未起票） {pendingBank} 件 ／ 支払期限7日以内の仕入先請求書 {dueSoon} 件 ／ 下書き伝票 {drafts} 件";

    // 経理: 当月 KPI（HomeKpiData = QueryField モジュールを検索して1行読む）
    var ks = new ModuleSearcher<HomeKpiData>();
    var kpiRows = ks.Execute();
    if (kpiRows.Count > 0)
    {
        var k = (HomeKpiData)kpiRows[0];
        var cash = k.CashBalance.Value ?? 0;
        var ar = k.ArBalance.Value ?? 0;
        var ap = k.ApBalance.Value ?? 0;
        var sales = k.MonthSales.Value ?? 0;
        var expense = k.MonthExpense.Value ?? 0;
        var profit = k.MonthProfit.Value ?? 0;
        KpiLine1.Text = $"現預金 {cash:#,0} 円 ／ 売掛金 {ar:#,0} 円 ／ 買掛金 {ap:#,0} 円";
        KpiLine2.Text = $"当月売上高 {sales:#,0} 円 ／ 当月費用 {expense:#,0} 円 ／ 当月利益 {profit:#,0} 円";
        if (profit < 0)
        {
            KpiLine2.Color = "#dc3545";
        }
    }

    // 経理: 資金繰りアラート（4ヶ月予測に ⚠ 行があれば表示）
    CashAlertLabel.Text = "";
    var cfs = new ModuleSearcher<CashFlowForecastData>();
    var forecast = cfs.Execute();
    foreach (var rm in forecast)
    {
        var r = (CashFlowForecastData)rm;
        if (r.AlertMark.Value != null && r.AlertMark.Value != "")
        {
            CashAlertLabel.Text = $"⚠ {r.MonthLabel.Value} に資金ショートの予測があります。「帳票 > 資金繰り予測」を確認してください";
            CashAlertLabel.Color = "#dc3545";
            break;
        }
    }
}
