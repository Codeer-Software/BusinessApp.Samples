// CashBook.mod.cs — 出納帳
// 検索初期値: 当年度の期首〜期末（初見UXテスト U3-4: 期間既定が無く全期間表示だった）
void Search_OnInitialization()
{
    var today = DateTime.Today;
    var firstDay = new DateTime(today.Year, today.Month, 1);
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var fy = s.ExecuteFirstOrDefault();
    if (fy == null)
    {
        // 今日がどの年度にも入らない日（期初に翌期を作り忘れた・年度の隙間）。
        // ここで諦めると期間が空のまま開き、SQL 側の `d_from` が NULL 比較になって**1 行も出ない**
        // ——「取引が無い」と区別できない（BUG-0097）。**直近の年度**へ縮退する
        // 「直近」は**直前に終わった年度**を指す（翌期を先に作ってあると、単純な降順では
        // まだ始まっていない年度が選ばれて結局ゼロになる）。無ければ最も早く始まる年度
        var s2 = new ModuleSearcher<FiscalYear>();
        s2.AddLessThan(e => e.EndDate.Value, today);
        s2.OrderByDescending(e => e.EndDate.Value);
        fy = s2.ExecuteFirstOrDefault();
        if (fy == null)
        {
            var s3 = new ModuleSearcher<FiscalYear>();
            s3.OrderBy(e => e.StartDate.Value);
            fy = s3.ExecuteFirstOrDefault();
        }
    }
    if (fy == null) return;
    var typed = (FiscalYear)fy;
    if (DateFrom.SearchMin == null) { DateFrom.SearchMin = typed.StartDate.Value; }
    if (DateTo.SearchMin == null) { DateTo.SearchMin = typed.EndDate.Value; }
}
