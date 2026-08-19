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
    // 縮退の判定は `FiscalYear.ResolveDisplayYear()` に寄せる（BUG-0444 の正典）——
    // 帳票ごとに書き写した結果、元帳と仕訳帳だけ縮退が無い状態になっていた
    var fy = new FiscalYear().ResolveDisplayYear();
    if (fy == null) return;
    FiscalYearRef.SearchValue = ((FiscalYear)fy).Id.Value;
}

void Search_OnInitialization()
{
    InitFiscalYearSearch();
}
