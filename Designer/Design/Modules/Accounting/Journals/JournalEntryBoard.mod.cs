// 振替伝票の一覧（入力のための一覧。ADR-0027）。
//
// **画面は軽いクエリ一覧（JournalEntryList）を検索欄と一緒に並べるだけ**で、
// 会計の判断は 1 行も持たない（ADR-0008）。取消・訂正の逆引きは SQL が引く。
//
// **CRUD モジュールの標準一覧をやめた理由**は 2 つある。
// ①「取り消されているか」は逆引きなので、原仕訳の行だけを見ても分からない（ADR-0027 §2）
// ②前回プロジェクトでは、伝票モジュール直の一覧が行ごとに重量級モジュール一式を組み立てて
//   **表示に約 4 秒**かかっていた（2026-07-20 実測。本プロジェクトではまだ計測していない）

// 表示専用モジュール（DbTable が空で CRUD 3 フラグが全部 false）の詳細画面は、
// 既定でビュー専用になり、**ボタンが押せなくなる**（qa/01 D-02）。ここで解除する。
void Detail_OnAfterInitialization()
{
    IsViewOnly = false;
}

// 新規作成。`-` が新規作成を表すことは本プロジェクトで確認済み（qa/04。2026-08-28）。
//
// **2 引数版は「現在のフレーム」で解決される**（ADR-0027）。この画面は Main フレームにしか
// 置いていないので `/Main/JournalEntry/-` に着く。**JournalEntry を Main の
// OtherPageModuleDesigns に登録していないと、着いた先が静かに真っ白になる**（qa/01 F-17）。
void NewButton_OnClick()
{
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("JournalEntry", "-"));
}
