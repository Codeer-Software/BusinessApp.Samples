// TrialBalance.mod.cs — 合計残高試算表
// 検索初期値: 当年度の期首〜期末（初見UXテスト U3-4/U3-8: 期間未指定だと期首繰越が乗らず
// 預金残高がマイナスに見える誤解を生むため、初期表示を当年度に固定する）
void Search_OnInitialization()
{
    var today = DateTime.Today;
    var firstDay = new DateTime(today.Year, today.Month, 1);
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var fy = s.ExecuteFirstOrDefault();
    if (fy == null) return;
    var typed = (FiscalYear)fy;
    if (DateFrom.SearchMin == null) { DateFrom.SearchMin = typed.StartDate.Value; }
    if (DateTo.SearchMin == null) { DateTo.SearchMin = typed.EndDate.Value; }
}
