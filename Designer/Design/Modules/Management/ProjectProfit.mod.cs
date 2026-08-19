// ProjectProfit.mod.cs — 案件別損益
//
// 検索の既定年度を入れるためだけのスクリプト（BUG-0448）。
// これが無いと会計年度が未選択のまま開き、**全案件が売上 0・粗利 0** で並ぶ。
// 「まだ取引が無い」のか「年度を選んでいないだけ」なのかが画面から区別できず、
// ペルソナ痛点 #1（案件別の採算が見えない）に答える画面が、初手で嘘の数字を出す。
//
// 年度の決め方は帳票 4 本と同じ `FiscalYear.ResolveDisplayYear()`（BUG-0444 の正典）。
// 期初に翌期の年度マスタを作り忘れていても直前に終わった年度へ縮退する。

void Search_OnInitialization()
{
    var fy = new FiscalYear().ResolveDisplayYear();
    if (fy == null) return;
    if (FiscalYearRef.SearchValue == null)
    {
        FiscalYearRef.SearchValue = ((FiscalYear)fy).Id.Value;
    }
}
