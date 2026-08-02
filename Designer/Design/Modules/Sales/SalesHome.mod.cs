// SalesHome.mod.cs — 営業業務部品のトップ（SalesStaff / SalesBilling 共通）
// ショートカット（同一フレーム内解決の AnchorTag）と、ほかの業務への切替ボタンを提供する。
// 参照するモジュールは営業部品内のみ（部品独立性の維持）。

void Detail_OnAfterInit()
{
    var name = "";
    if (CurrentUser != null && CurrentUser.表示名.Value != null)
    {
        name = CurrentUser.表示名.Value;
    }
    GreetingLabel.Text = $"こんにちは、{name} さん";
    DateLabel.Text = $"{DateTime.Today:yyyy年M月d日}";
}

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
