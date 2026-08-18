// PortalHome.mod.cs — 業務ポータル（ADR-0042 で着地一元化・ADR-0045 で「やること集約」へ全面改装）
// 「この機能を使う権限があるなら、これを表示する」の宣言的合成（正典: docs/13 §3）。
// 業務への導線は左サイドバー（PortalSidebar）に一本化し、本画面は
// 自分あて（通知・申請・承認待ち）→ 経理の作業キュー → アラート → KPI を上から並べる。
// 部品独立性: 参照は AppUser と Shell 所有の Portal*Data（契約 SQL）のみ。遷移は URL のみ。
// 0 件の項目は行ごと非表示（アラート・キューは「対応が要るときだけ見える」）。

void Detail_OnAfterInit()
{
    // 表示専用モジュールの Detail はビュー専用扱いになりクリック不能になる（実測）。明示解除する
    IsViewOnly = false;

    var name = "";
    if (CurrentUser != null && CurrentUser.表示名.Value != null)
    {
        name = CurrentUser.表示名.Value;
    }
    GreetingLabel.Text = $"こんにちは、{name} さん";
    DateLabel.Text = $"{DateTime.Today:yyyy年M月d日}";

    var isAdmin = CurrentUser.IsSysAdmin.Value == true;
    var hasAccounting = CurrentUser.HasAccountingAccess.Value == true;
    var isApprover = CurrentUser.IsApprover.Value == true;
    var hasSales = CurrentUser.HasSalesAccess.Value == true;
    var canExpense = CurrentUser.CanUseExpense.Value == true;

    // ---- 自分あて（承認待ち・進行中の申請） ----
    MyApprovalsLink.IsVisible = false;
    MyApplyingLink.IsVisible = false;
    if (isApprover || hasAccounting || canExpense)
    {
        var myApprovals = 0;
        var myApplying = 0;
        var ts = new ModuleSearcher<PortalTodoData>();
        var todoRows = ts.Execute();
        foreach (var r in todoRows)
        {
            var t = (PortalTodoData)r;
            if ($"{t.UserId.Value}" == $"{CurrentUser.Id.Value}")
            {
                myApprovals = (int)(t.MyApprovals.Value ?? 0);
                myApplying = (int)(t.MyApplying.Value ?? 0);
            }
        }
        if (isApprover || hasAccounting)
        {
            MyApprovalsLink.IsVisible = true;
            MyApprovalsLink.Text = $"▶ あなたの承認待ち: {myApprovals} 件";
        }
        if (canExpense)
        {
            MyApplyingLink.IsVisible = true;
            MyApplyingLink.Text = $"▶ 進行中のあなたの申請: {myApplying} 件";
        }
    }

    // ---- 経理の作業キュー ----
    QueueSectionLabel.IsVisible = false;
    SettlementQueueLink.IsVisible = false;
    BankPendingLink.IsVisible = false;
    JournalDraftsLink.IsVisible = false;
    BillingPendingLink.IsVisible = false;
    SesPendingLink.IsVisible = false;
    SesNoTimesheetLink.IsVisible = false;
    if (hasAccounting)
    {
        var qs = new ModuleSearcher<PortalQueueData>();
        var qRows = qs.Execute();
        if (qRows.Count > 0)
        {
            var q = (PortalQueueData)qRows[0];
            var settlement = (int)(q.SettlementQueue.Value ?? 0);
            var bank = (int)(q.BankPending.Value ?? 0);
            var drafts = (int)(q.JournalDrafts.Value ?? 0);
            // 定期請求と SES は実行するモジュールが別（RecurringRun / SesBilling）なので、
            // 件数も導線も分ける。合算すると「3 件と言われた画面に 2 件しかない」ことが起きる（ADR-0060）
            var recurring = (int)(q.RecurringPending.Value ?? 0);
            var ses = (int)(q.SesPending.Value ?? 0);
            var sesNoTime = (int)(q.SesNoTimesheet.Value ?? 0);
            if (settlement > 0)
            {
                SettlementQueueLink.IsVisible = true;
                SettlementQueueLink.Text = $"▶ 精算処理待ちの経費: {settlement} 件";
            }
            if (bank > 0)
            {
                BankPendingLink.IsVisible = true;
                BankPendingLink.Text = $"▶ 未起票の銀行明細: {bank} 件";
            }
            if (drafts > 0)
            {
                JournalDraftsLink.IsVisible = true;
                JournalDraftsLink.Text = $"▶ 下書きのままの伝票: {drafts} 件";
            }
            if (recurring > 0)
            {
                BillingPendingLink.IsVisible = true;
                BillingPendingLink.Text = $"▶ 定期請求の当月未生成: {recurring} 件";
            }
            if (ses > 0)
            {
                SesPendingLink.IsVisible = true;
                SesPendingLink.Text = $"▶ SES 請求の当月未生成: {ses} 件";
            }
            // 実績 0h の月を請求対象から外した以上（ADR-0060）、「請求が出てこない理由」を
            // ここで見せないと黙って落ちる。請求とは別の作業なので行も遷移先も分ける
            if (sesNoTime > 0)
            {
                SesNoTimesheetLink.IsVisible = true;
                SesNoTimesheetLink.Text = $"▶ SES の当月工数が未入力: {sesNoTime} 件（入力されるまで請求を作れません）";
            }
            QueueSectionLabel.IsVisible = settlement > 0 || bank > 0 || drafts > 0
                || recurring > 0 || ses > 0 || sesNoTime > 0;
        }
    }

    // ---- アラート ----
    AlertSectionLabel.IsVisible = false;
    PayDueLink.IsVisible = false;
    ReceivableOverdueLink.IsVisible = false;
    CashAlertLink.IsVisible = false;
    BudgetAlertLink.IsVisible = false;
    if (hasSales || isApprover || hasAccounting)
    {
        var als = new ModuleSearcher<PortalAlertData>();
        var aRows = als.Execute();
        if (aRows.Count > 0)
        {
            var a = (PortalAlertData)aRows[0];
            var payOver = (int)(a.PayOverdue.Value ?? 0);
            var paySoon = (int)(a.PaySoon.Value ?? 0);
            var recvOver = (int)(a.ReceivableOverdue.Value ?? 0);
            var cashMonths = (int)(a.CashAlertMonths.Value ?? 0);
            var cashWarnMonths = (int)(a.CashWarnMonths.Value ?? 0);
            var budgetDepts = (int)(a.BudgetAlertDepts.Value ?? 0);
            var soonDays = (int)(a.DueSoonDays.Value ?? 7);
            if (hasAccounting && (payOver > 0 || paySoon > 0))
            {
                PayDueLink.IsVisible = true;
                PayDueLink.Text = $"⚠ 支払期限: 超過 {payOver} 件 ／ {soonDays}日以内 {paySoon} 件";
            }
            if ((hasSales || hasAccounting) && recvOver > 0)
            {
                ReceivableOverdueLink.IsVisible = true;
                ReceivableOverdueLink.Text = $"⚠ 期限超過の売掛: {recvOver} 件";
            }
            // 資金の警告は 2 段階（BUG-0249）。**ショートと危険水域を 1 つの件数に混ぜない**——
            // 混ぜると黒字の月まで「ショート」と表示され、予測画面の「△ 危険水域」と重大度が食い違う
            if ((isApprover || hasAccounting) && (cashMonths > 0 || cashWarnMonths > 0))
            {
                CashAlertLink.IsVisible = true;
                if (cashMonths > 0)
                {
                    var more = (cashWarnMonths > 0) ? $"（ほかに危険水域 {cashWarnMonths} ヶ月）" : "";
                    CashAlertLink.Text = $"⚠ 資金ショート予測: 今後4ヶ月中 {cashMonths} ヶ月{more}";
                }
                else
                {
                    CashAlertLink.Text = $"△ 資金が危険水域: 今後4ヶ月中 {cashWarnMonths} ヶ月";
                }
            }
            // 予算警告の表示範囲（2026-08-06 ユーザー仕様）:
            // 経理 = どこかの部門に警告があれば表示（全部門を横断で見る役割）
            // 非経理の承認者 = 自分の所属部に警告がある時だけ表示（他部門の警告は自分の仕事ではない）
            if (budgetDepts > 0)
            {
                if (hasAccounting)
                {
                    BudgetAlertLink.IsVisible = true;
                    BudgetAlertLink.Text = $"⚠ 予算警告: {budgetDepts} 部門";
                }
                else if (isApprover)
                {
                    var alertDeptIds = $",{a.BudgetAlertDeptIds.Value},";
                    var myDept = $"{CurrentUser.所属部.Value}";
                    if (myDept != "" && alertDeptIds.Contains($",{myDept},"))
                    {
                        BudgetAlertLink.IsVisible = true;
                        // CurrentUser の SelectField は候補未ロードで DisplayText が空（実測）→ 部門マスタから名前を引く
                        var deptName = "";
                        var ds = new ModuleSearcher<Department>();
                        ds.AddEquals(d => d.Id.Value, CurrentUser.所属部.Value);
                        var deptRow = ds.ExecuteFirstOrDefault();
                        if (deptRow != null)
                        {
                            deptName = ((Department)deptRow).Name.Value;
                        }
                        BudgetAlertLink.Text = deptName != ""
                            ? $"⚠ 予算警告: あなたの部門（{deptName}）"
                            : "⚠ 予算警告: あなたの部門";
                    }
                }
            }
            AlertSectionLabel.IsVisible = PayDueLink.IsVisible || ReceivableOverdueLink.IsVisible
                || CashAlertLink.IsVisible || BudgetAlertLink.IsVisible;
        }
    }

    // ---- KPI（経理のみ・リスクの高い読み取りは最後に置く規律） ----
    KpiSectionLabel.IsVisible = hasAccounting;
    KpiLine1.IsVisible = false;
    KpiLine2.IsVisible = false;
    if (hasAccounting)
    {
        var ks = new ModuleSearcher<PortalKpiData>();
        var kpiRows = ks.Execute();
        if (kpiRows.Count > 0)
        {
            var k = (PortalKpiData)kpiRows[0];
            var cash = k.CashBalance.Value ?? 0;
            var ar = k.ArBalance.Value ?? 0;
            var ap = k.ApBalance.Value ?? 0;
            var sales = k.MonthSales.Value ?? 0;
            var expense = k.MonthExpense.Value ?? 0;
            var profit = k.MonthProfit.Value ?? 0;
            KpiLine1.IsVisible = true;
            KpiLine2.IsVisible = true;
            KpiLine1.Text = $"現預金 {cash:#,0} 円 ／ 売掛金 {ar:#,0} 円 ／ 買掛金 {ap:#,0} 円";
            KpiLine2.Text = $"当月売上高 {sales:#,0} 円 ／ 当月費用 {expense:#,0} 円 ／ 当月利益 {profit:#,0} 円";
            if (profit < 0)
            {
                KpiLine2.Color = "#dc3545";
            }
        }
    }

    // ---- システム管理者への案内（業務フラグを持たないのが既定＝職務分掌） ----
    AdminNoteLink.IsVisible = isAdmin;
}

// ---- 変種フレームの解決（PortalSidebar と同じ規約: 経理 > 承認者 > 一般） ----

string ResolveExpenseFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "ExpenseAccounting";
    if (CurrentUser.IsApprover.Value == true) return "ExpenseApprover";
    return "ExpenseStaff";
}

string ResolveSalesFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "SalesBilling";
    return "SalesStaff";
}

// 承認待ちを持つのは承認者/経理のみ（表示条件と対）。Staff へのフォールバックを持たない専用リゾルバ
string ResolveApprovalFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "ExpenseAccounting";
    return "ExpenseApprover";
}

string ResolveManagementFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "ManagementFull";
    return "ManagementApprover";
}

// ---- 遷移（各項目のクリックで該当一覧へ） ----

void MyApprovals_OnClick()
{
    NavigationService.NavigateTo($"/{ResolveApprovalFrame()}/ApprovalInbox");
}

void MyApplying_OnClick()
{
    NavigationService.NavigateTo($"/{ResolveExpenseFrame()}/MyApplication");
}

void AllNotifications_OnClick()
{
    NavigationService.NavigateTo("/Main/Notification?initialize_search=true");
}

void SettlementQueue_OnClick()
{
    NavigationService.NavigateTo("/ExpenseAccounting/ExpenseSettlementQueue");
}

void BankPending_OnClick()
{
    NavigationService.NavigateTo("/Accounting/BankPosting");
}

void JournalDrafts_OnClick()
{
    NavigationService.NavigateTo("/Accounting/JournalEntryBoard");
}

void BillingPending_OnClick()
{
    NavigationService.NavigateTo("/SalesBilling/RecurringRun");
}

void SesPending_OnClick()
{
    NavigationService.NavigateTo("/SalesBilling/SesBilling");
}

// 未入力の実体は工数側だが、まず「どの案件が止まっているか」を理由つきで見せたいので
// SES 精算・請求のプラン一覧へ送る（対象外の行に理由が出ている）
void SesNoTimesheet_OnClick()
{
    NavigationService.NavigateTo("/SalesBilling/SesBilling");
}

void PayDue_OnClick()
{
    NavigationService.NavigateTo("/Purchasing/PaymentSchedule");
}

void ReceivableOverdue_OnClick()
{
    NavigationService.NavigateTo($"/{ResolveSalesFrame()}/ReceivableBalance");
}

void CashAlert_OnClick()
{
    // 資金繰り予測の画面は経営管理（経理）のみ。承認者にはアラート表示のみで詳細画面が無い
    if (CurrentUser.HasAccountingAccess.Value == true)
    {
        NavigationService.NavigateTo("/ManagementFull/CashFlowForecast");
        return;
    }
    Toaster.Info("資金繰り予測の詳細は経理アクセスを持つユーザーが確認できます");
}

void BudgetAlert_OnClick()
{
    // initialize_search=true で予実対比の既定検索（現在年度＋非経理は自部門）を発火させる（#48）
    NavigationService.NavigateTo($"/{ResolveManagementFrame()}/BudgetVsActual?initialize_search=true");
}

void AdminNote_OnClick()
{
    NavigationService.NavigateTo("/MasterAdmin");
}
