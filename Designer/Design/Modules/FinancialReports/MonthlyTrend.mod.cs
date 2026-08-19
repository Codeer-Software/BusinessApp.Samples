// 月次推移表: 検索初期値（サイドバーから開いたとき PL を既定選択にする。
// 未選択でも SQL 側の COALESCE で PL 扱いになるため、これは表示の明示のみ）
void Search_OnInitialization()
{
    InitFiscalYearSearch();
    StatementSel.SearchValue = "PL";
    WarnIrregularPeriods();
}

// 対象年度を必ず画面に出す（BUG-0113）。
// 旧実装は年度欄が空のまま SQL 側で「今日を含む年度」を解決していたので、
//   ①いま**どの年度を見ているのか画面から分からない**（空欄なのに中身は入っている）
//   ②今日がどの年度にも入らない日（期初に翌期を作り忘れた・年度の隙間）に開くと、
//     内側の SELECT が NULL になって**もっともらしいゼロの財務諸表**が出る
// という 2 つの問題があった。
// 縮退の判定は `FiscalYear.ResolveDisplayYear()` に寄せる（BUG-0444 の正典）——
// 帳票ごとに書き写した結果、元帳と仕訳帳だけ縮退が無い状態になっていた。
// 注意: 検索の初期化は `?initialize_search=true` 付きの遷移でしか発火しない（ADR-0057）。
void InitFiscalYearSearch()
{
    if (FiscalYearRef.SearchValue != null) return;
    var fy = new FiscalYear().ResolveDisplayYear();
    if (fy == null) return;
    FiscalYearRef.SearchValue = ((FiscalYear)fy).Id.Value;
}

// 12 列に収まらない年度を画面で言う（BUG-0111）。
//
// 画面の月列は m01〜m12 の**12 本固定**（CLB のフィールドは可変にできない）。
// ところが `fiscal_periods` に 12 本という制約は無く、決算期変更に伴う
// 13〜18 ヶ月の変則決算期がありうる。SQL 側は
//   ・PL は 12 列目に第 12 月以降を畳む（12 列の合計＝Total を保つ）
//   ・BS は最終期間の残高を「期末」に出す
// という形に直したが、**「12 列目が第 12 月だけではない」ことは画面からは分からない**。
// 数字は合っているが読み方が変わるので、そこだけ言う。
// 逆に 12 未満の短い決算期では余った列が空欄で並ぶ——これも黙っていると「データが無い」に見える
void WarnIrregularPeriods()
{
    PeriodWarnLabel.Text = "";
    PeriodWarnLabel.Color = "";
    if (FiscalYearRef.SearchValue == null) return;

    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddEquals(e => e.FiscalYearId.Value, FiscalYearRef.SearchValue);
    var n = ps.Execute().Count;
    if (n == 12 || n == 0) return;

    if (n > 12)
    {
        PeriodWarnLabel.Text = $"⚠ この年度は月次期間が {n} ヶ月あります（変則決算期）。"
            + "月列は 12 本しかないため、**第 12 月の列には第 12 月以降がまとめて入って**います"
            + "（PL は 12 列の合計＝右端の合計。BS の「合計/期末」は最終期間の残高）";
    }
    else
    {
        PeriodWarnLabel.Text = $"⚠ この年度は月次期間が {n} ヶ月しかありません（変則決算期）。"
            + $"第 {n + 1} 月以降の列は空欄で並びます（データが無いのではなく、期間がありません）";
    }
    PeriodWarnLabel.Color = "#dc3545";
}
