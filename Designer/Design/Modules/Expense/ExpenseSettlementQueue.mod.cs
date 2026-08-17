// ExpenseSettlementQueue.mod.cs — 精算処理待ち（経理の作業受信箱。初見UXテスト U3-3）
// expense_request の読み取り専用ビュー。承認済/経理処理中/精算済（=経理の作業対象）だけを表示し、
// 「開く」で経理用の申請画面（ExpenseRequestAccounting）へ遷移する。
//
// 遷移先が ExpenseRequest（申請者用）でないのは ADR-0069 による。申請者用には
// 行条件（Creator == CurrentUser）が入る予定で、経理はそこから他人の申請を開けない。
void Open_OnClick()
{
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("ExpenseRequestAccounting", $"{Id.Value}"));
}
