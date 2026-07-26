// JournalBook.mod.cs — 仕訳帳
// 検索初期値: 当年度の期首〜期末（初見UXテスト U3-4: 期間既定が無く全期間表示だった）
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
