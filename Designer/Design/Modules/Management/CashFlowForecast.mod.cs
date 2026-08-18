// CashFlowForecast.mod.cs — 資金繰り予測（画面の注意書きだけを担う）
//
// 予測そのものは `CashFlowForecastData.Query.sql`（当月含む今後 4 ヶ月）。
// ここでやるのは 1 つだけ——**人件費コストが未登録の月があることを画面に出す**（BUG-0346）。
//
// 出金の人件費は `monthly_salaries`（年度 × 月 × 社員）から拾う。登録が無い月は**静かに 0 円**になり、
// 期末資金が実態より多く見える。実データでも第18期は 7 月分しか登録が無く、
// **9〜11 月の出金が 3 ヶ月とも 0 円**になっていた（＝資金ショートも危険水域も永久に鳴らない）。
// 金額を勝手に補うことはできないので、気づけるようにする。配賦画面の「⚠未配賦」と同じ考え方。

void Detail_OnAfterInit()
{
    SalaryWarnLabel.Text = "";
    SalaryWarnLabel.Color = "";

    var today = DateTime.Today;
    var missing = "";
    var missingCount = 0;
    var i = 0;
    while (i < 4)
    {
        var target = new DateOnly(today.Year, today.Month, 1).AddMonths(i);
        i = i + 1;
        // その月の月次期間（＝年度と月No）を引く。無い月は予測の対象外なので触れない
        var ps = new ModuleSearcher<FiscalPeriod>();
        ps.AddLessThanOrEqual(e => e.StartDate.Value, target);
        ps.AddGreaterThanOrEqual(e => e.EndDate.Value, target);
        var period = ps.ExecuteFirstOrDefault();
        if (period == null) continue;
        var fp = (FiscalPeriod)period;

        var ms = new ModuleSearcher<MonthlySalary>();
        ms.AddEquals(e => e.FiscalYearRef.Value, fp.FiscalYearId.Value);
        ms.AddEquals(e => e.PeriodNo.Value, fp.PeriodNo.Value);
        if (ms.Execute().Count > 0) continue;

        missingCount = missingCount + 1;
        if (missing != "") { missing = missing + "・"; }
        missing = missing + $"{target:yyyy年M月}";
    }

    if (missingCount == 0) return;
    SalaryWarnLabel.Text = $"⚠ 人件費コストが未登録の月があります（{missing}）。"
        + "その月の出金には人件費が乗らないため、期末資金が実態より多く見えます"
        + "（資金ショート・危険水域の警告も鳴りません）。"
        + "経営管理 > 案件損益 > 人件費コスト で登録してください";
    SalaryWarnLabel.Color = "#dc3545";
}
