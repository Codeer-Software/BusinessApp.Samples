// PortalSidebar.mod.cs — Main フレームの左サイドバー（ADR-0045。SideBarDesign.ModuleName で標準 UI を置換）
// 業務への導線を一本化する。表示条件は権限フラグのみ（ADR-0043）、
// 同一部品の変種フレームは権限の強い順に解決する（経理 > 承認者 > 一般）。
// 遷移は URL のみ（部品のモジュール型を参照しない＝部品独立性の維持）。
// 標準サイドバーの Logout はモジュール化で消えるため自前で持つ（NavigationService.Logout）。

void Detail_OnAfterInit()
{
    // 表示専用モジュールの Detail はビュー専用扱いになりリンクが押せなくなる（PortalHome と同じ実測）
    IsViewOnly = false;

    // 通知（全員）: 未読があれば件数バッジつきで表示（DataReadCondition により自分宛のみが数えられる）
    var ns = new ModuleSearcher<Notification>();
    ns.AddEquals(n => n.IsRead.Value, false);
    var unread = ns.Execute().Count;
    NotificationsLink.Text = unread > 0 ? $"通知 ({unread})" : "通知";

    var hasAccounting = CurrentUser.HasAccountingAccess.Value == true;
    GoExpenseLink.IsVisible = CurrentUser.CanUseExpense.Value == true;
    GoTimesheetLink.IsVisible = CurrentUser.CanUseTimesheet.Value == true;
    GoSalesLink.IsVisible = CurrentUser.HasSalesAccess.Value == true || hasAccounting;
    GoAccountingLink.IsVisible = hasAccounting;
    GoPurchasingLink.IsVisible = hasAccounting;
    GoManagementLink.IsVisible = CurrentUser.IsApprover.Value == true || hasAccounting;
    GoMasterLink.IsVisible = hasAccounting;
    GoAdminLink.IsVisible = CurrentUser.IsSysAdmin.Value == true;
}

// ---- 変種フレームの解決（権限の強い順） ----

string ResolveExpenseFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "ExpenseAccounting";
    if (CurrentUser.IsApprover.Value == true) return "ExpenseApprover";
    return "ExpenseStaff";
}

string ResolveTimesheetFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "TimesheetAccounting";
    return "Timesheet";
}

string ResolveSalesFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "SalesBilling";
    return "SalesStaff";
}

string ResolveManagementFrame()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return "ManagementFull";
    return "ManagementApprover";
}

// ---- 遷移（フレーム素の URL = 各フレームの既定業務画面に着地・ADR-0045 でトップ廃止） ----

void GoExpense_OnClick()
{
    NavigationService.NavigateTo($"/{ResolveExpenseFrame()}");
}

void GoTimesheet_OnClick()
{
    NavigationService.NavigateTo($"/{ResolveTimesheetFrame()}");
}

void GoSales_OnClick()
{
    NavigationService.NavigateTo($"/{ResolveSalesFrame()}");
}

void GoAccounting_OnClick()
{
    NavigationService.NavigateTo("/Accounting");
}

void GoPurchasing_OnClick()
{
    NavigationService.NavigateTo("/Purchasing");
}

void GoManagement_OnClick()
{
    NavigationService.NavigateTo($"/{ResolveManagementFrame()}");
}

void GoMaster_OnClick()
{
    NavigationService.NavigateTo("/MasterBusiness");
}

void GoAdmin_OnClick()
{
    NavigationService.NavigateTo("/MasterAdmin");
}

void GoHome_OnClick()
{
    NavigationService.NavigateTo("/Main/PortalHome");
}

void Notifications_OnClick()
{
    NavigationService.NavigateTo("/Main/Notification?initialize_search=true");
}

// 利用者自身の設定（ADR-0059）。全員に出す＝パスワード変更を管理者に依頼する運用をやめる。
// サイドバーには「パスワードの変更」を直接出さず「設定」フレームを 1 枚かませる
// （業務メニューと同じ高さに破壊的な操作を並べない・2026-08-16 ユーザー指示）
void GoSettings_OnClick()
{
    NavigationService.NavigateTo("/Settings");
}

void Logout_OnClick()
{
    NavigationService.Logout();
}
