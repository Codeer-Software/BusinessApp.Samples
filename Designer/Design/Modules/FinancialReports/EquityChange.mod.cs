// 対象年度を必ず画面に出す（BUG-0113）。
// 旧実装は年度欄が空のまま SQL 側で「今日を含む年度」を解決していたので、
//   ①いま**どの年度を見ているのか画面から分からない**（空欄なのに中身は入っている）
//   ②今日がどの年度にも入らない日（期初に翌期を作り忘れた・年度の隙間）に開くと、
//     内側の SELECT が NULL になって**もっともらしいゼロの財務諸表**が出る
// という 2 つの問題があった。今日を含む年度、無ければ**直近の年度**を初期値に入れる。
// これは既定値なので、利用者が別の年度を選べば当然そちらが優先される。
// 注意: 検索の初期化は `?initialize_search=true` 付きの遷移でしか発火しない（ADR-0057）。
//       サイドバー・ポータルのリンクは付いているので通常の導線では効く。
void InitFiscalYearSearch()
{
    if (FiscalYearRef.SearchValue != null) return;

    var today = DateTime.Today;
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, today);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, today);
    var fy = s.ExecuteFirstOrDefault();

    if (fy == null)
    {
        // 今日がどの年度にも入らない日。**黙ってゼロを出さず、直近の年度を見せる**
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

    FiscalYearRef.SearchValue = ((FiscalYear)fy).Id.Value;
}

void Search_OnInitialization()
{
    InitFiscalYearSearch();
}
