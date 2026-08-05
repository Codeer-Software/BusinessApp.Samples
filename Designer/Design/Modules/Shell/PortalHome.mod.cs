// PortalHome.mod.cs — ルートフレーム（Main）のトップページ＝ログイン直後の業務ポータル（ADR-0042）
// CLB は「IsApplicationRoot のフレームは全員アクセス可能」が前提のため、ルートには業務データを置かず、
// アクセスできる部品（業務）へのタイルだけを並べる（旧 RoleDispatch の権限別自動転送を置換）。
// 権限はロールではなく AppUser のキャッシュ列（部門メンバーシップ＋管理者フラグから DB トリガーが導出）で判定する。
// 参照は AppUser（レイヤ0）のみ・遷移は URL のみ（部品のモジュール型を参照しない＝部品独立性の維持）。

void Detail_OnAfterInit()
{
    // 表示専用モジュール（DbTable 無し・CanCreate/Update false）の Detail はビュー専用扱いになり、
    // ボタンが pointer-events:none で描画されてクリック不能になる（実測）。明示解除する
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

    // 全ボタンが権限フラグだけで決まる（ADR-0043。admin は業務フラグ OFF がシード既定＝職務分掌）
    GoExpenseBtn.IsVisible = CurrentUser.CanUseExpense.Value == true;
    GoTimesheetBtn.IsVisible = CurrentUser.CanUseTimesheet.Value == true;
    GoSalesBtn.IsVisible = hasSales || hasAccounting;
    GoAccountingBtn.IsVisible = hasAccounting;
    GoPurchasingBtn.IsVisible = hasAccounting;
    GoManagementBtn.IsVisible = isApprover || hasAccounting;
    GoMasterBusinessBtn.IsVisible = hasAccounting;
    GoAdminBtn.IsVisible = isAdmin;
}

// 各部品への遷移。同一部品の変種フレームは権限の強い順に解決する（経理 > 承認者 > 一般）

void GoExpenseBtn_OnClick()
{
    var frame = "ExpenseStaff";
    if (CurrentUser.HasAccountingAccess.Value == true) { frame = "ExpenseAccounting"; }
    else if (CurrentUser.IsApprover.Value == true) { frame = "ExpenseApprover"; }
    NavigationService.NavigateTo($"/{frame}/Top");
}

void GoTimesheetBtn_OnClick()
{
    var frame = "Timesheet";
    if (CurrentUser.HasAccountingAccess.Value == true) { frame = "TimesheetAccounting"; }
    NavigationService.NavigateTo($"/{frame}/Top");
}

void GoSalesBtn_OnClick()
{
    var frame = "SalesStaff";
    if (CurrentUser.HasAccountingAccess.Value == true) { frame = "SalesBilling"; }
    NavigationService.NavigateTo($"/{frame}/Top");
}

void GoAccountingBtn_OnClick()
{
    NavigationService.NavigateTo("/Accounting/Top");
}

void GoPurchasingBtn_OnClick()
{
    NavigationService.NavigateTo("/Purchasing/Top");
}

void GoManagementBtn_OnClick()
{
    var frame = "ManagementApprover";
    if (CurrentUser.HasAccountingAccess.Value == true) { frame = "ManagementFull"; }
    NavigationService.NavigateTo($"/{frame}/Top");
}

void GoMasterBusinessBtn_OnClick()
{
    NavigationService.NavigateTo("/MasterBusiness/Top");
}

void GoAdminBtn_OnClick()
{
    NavigationService.NavigateTo("/MasterAdmin/Top");
}
