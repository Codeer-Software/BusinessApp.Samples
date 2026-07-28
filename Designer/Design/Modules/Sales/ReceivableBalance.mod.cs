// ReceivableBalance.mod.cs — 売掛残高（クエリ専用モジュール）の検索初期値
// 既定で「入金済を除く」を選択して開く（未回収債権の把握が主目的のため）。
// OnSearchInitialization はサイドバー Link 経由（?initialize_search=true 付与）で発火する。
void Search_OnSearchInitialization()
{
    StateFilter.SearchValue = "exclude_paid";
}
