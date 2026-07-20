// ReceiptBoard.mod.cs — 入金一覧（カスタム一覧）
// 責務: 消込状態を読み取り時導出するクエリ一覧（ReceiptList）を、検索＋新規作成ボタン付きで表示する。
// 消込済みか否かの真実は「消込仕訳の有無」であり、DB に状態列は持たない（2026-07-21 ユーザー決定）。

void NewButton_OnClick()
{
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("Receipt", "-"));
}
