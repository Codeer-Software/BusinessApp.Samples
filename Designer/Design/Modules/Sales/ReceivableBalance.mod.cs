// ReceivableBalance.mod.cs — 売掛残高（クエリ専用モジュール）の検索初期値
// 既定で「入金済を除く」を選択して開く（未回収債権の把握が主目的のため）。
// OnSearchInitialization はサイドバー Link 経由（?initialize_search=true 付与）で発火する。
void Search_OnSearchInitialization()
{
    // ポータルの「⚠ 期限超過の売掛: N 件」から来たときは、その条件で絞って着地する（BUG-0256）。
    // **URL パラメータで明示的に受け取る**——「initialize_search が来たら期限超過」にすると、
    // サイドバーから開いたときまで絞られてしまう
    var qs = NavigationService.GetUniqueQueryParameters();
    if (qs != null && qs.ContainsKey("state"))
    {
        var raw = qs["state"];
        if (!string.IsNullOrEmpty(raw))
        {
            StateFilter.SearchValue = raw;
            return;
        }
    }
    StateFilter.SearchValue = "exclude_paid";
}
