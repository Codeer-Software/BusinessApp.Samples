// ExpenseSettlementQueue.mod.cs — 精算処理待ち（経理の作業受信箱。初見UXテスト U3-3）
// expense_request の読み取り専用ビュー。承認済/経理処理中/精算済（=経理の作業対象）だけを表示し、
// 「開く」で経費申請の詳細（経理ボタンのある画面）へ遷移する。
void Open_OnClick()
{
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("ExpenseRequest", $"{Id.Value}"));
}
