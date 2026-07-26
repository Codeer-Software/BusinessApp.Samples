// JournalEntryBoard.mod.cs — 振替伝票一覧（カスタム一覧）
// 責務: 軽量クエリ一覧（JournalEntryList・借方合計の金額列つき）を検索＋新規作成ボタン付きで表示する。
// 旧来の「JournalEntry モジュール直の一覧ページ」は行ごとに重量級モジュール一式を構築して
// 表示に約4秒かかっていた（2026-07-20 実測）。帳簿系と同じクエリ一覧に置き換えて解消する。

void NewButton_OnClick()
{
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("JournalEntry", "-"));
}
