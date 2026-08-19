// JournalEntryList.mod.cs — 振替伝票一覧（検索の初期値だけを担う）
//
// ポータルの「▶ 下書きのままの伝票: N 件」から来たときに、状態＝下書きで絞った状態で着地させる
// （BUG-0440）。素で遷移していた頃は、3 件と言われて開いた画面に確定伝票を含む直近 50 件が並び、
// 利用者が自分で状態を選び直さないと N 件を特定できなかった。
// ADR-0060 が戒めた「N 件と言われた画面に N 件が無い」の再生産。
//
// **URL パラメータで明示的に受け取る。** 「initialize_search が来たら下書き」にすると、
// ほかの導線が initialize_search を付けた瞬間に意図しない絞り込みが効く。
// サイドバーからの通常遷移（パラメータなし）では何もしないので、全件表示のまま変わらない。

void Search_OnInitialization()
{
    var qs = NavigationService.GetUniqueQueryParameters();
    if (qs == null) return;
    if (!qs.ContainsKey("status")) return;
    var raw = qs["status"];
    if (string.IsNullOrEmpty(raw)) return;
    StatusFilter.SearchValue = raw;
}
