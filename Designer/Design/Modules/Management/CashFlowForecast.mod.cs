// CashFlowForecast.mod.cs — 資金繰り予測（画面の注意書きだけを担う）
//
// 予測そのものは `CashFlowForecastData.Query.sql`（当月含む今後 4 ヶ月）。
// ここでやるのは 1 つだけ——**出金の人件費が信用できない月があることを画面に出す**（BUG-0346）。
//
// 出金の人件費は `monthly_salaries`（年度 × 月 × 社員）から拾う。登録が無ければ**静かに 0 円**になり、
// 期末資金が実態より多く見える（＝資金ショートも危険水域も鳴らない）。金額を勝手に補うことはできないので、
// 気づけるようにする。配賦画面の「⚠未配賦」と同じ考え方。
//
// 判定の粒度は `v_missing_salary`（工数はあるのに人件費が無い**人×月**）に合わせる（BUG-0432）。
// 「その月に 1 行でもあれば登録済み」にすると、社員 30 名中 1 名だけ登録された月を見逃す。
// 仕掛品（v_wip_status）・案件別損益（ProjectProfit）と**同じ定義**であることが要点で、
// ここだけ粗いと「片方は警告、片方は無言」になる。

void Detail_OnAfterInit()
{
    SalaryWarnLabel.Text = "";
    SalaryWarnLabel.Color = "";

    var today = DateTime.Today;
    var missing = "";       // 人件費が一部でも欠けている月
    var noPeriod = "";      // 月次期間そのものが未作成の月
    var i = 0;
    while (i < 4)
    {
        var target = new DateOnly(today.Year, today.Month, 1).AddMonths(i);
        i = i + 1;

        // その月の月次期間（＝年度と月No）を引く。
        // **無い月を黙って飛ばさない**（BUG-0431）——予測本体の月リストは fiscal_periods とは無関係に
        // 必ず 4 行出るのに、出金側の sal_out は `JOIN fiscal_periods` なので**人件費 0 円**になる。
        // 翌年度の月次期間を作り忘れたまま年度末をまたぐと（4 ヶ月予測なので年に 3 回必ず起きる）、
        // その月の人件費が丸ごと落ちて、しかも警告も出ない
        var ps = new ModuleSearcher<FiscalPeriod>();
        ps.AddLessThanOrEqual(e => e.StartDate.Value, target);
        ps.AddGreaterThanOrEqual(e => e.EndDate.Value, target);
        var period = ps.ExecuteFirstOrDefault();
        if (period == null)
        {
            if (noPeriod != "") { noPeriod = noPeriod + "・"; }
            noPeriod = noPeriod + $"{target:yyyy年M月}";
            continue;
        }
        var fp = (FiscalPeriod)period;

        var lack = MissingSalaryUserCount(fp);
        if (lack == 0) continue;
        if (missing != "") { missing = missing + "・"; }
        missing = missing + $"{target:yyyy年M月}（{lack} 名分）";
    }

    var text = "";
    if (missing != "")
    {
        text = $"⚠ 人件費コストが未登録の社員がいる月があります（{missing}）。"
            + "その分の出金に人件費が乗らないため、期末資金が実態より多く見えます"
            + "（資金ショート・危険水域の警告も鳴りません）。"
            + "経営管理 > 人件費コスト で登録してください";
    }
    if (noPeriod != "")
    {
        if (text != "") { text = text + "  "; }
        text = text + $"⚠ 月次期間が未作成の月があります（{noPeriod}）。"
            + "その月の出金には人件費が一切乗りません。"
            + "業務マスタ > 会計年度 で月次期間を作成してください";
    }
    if (text == "") return;

    SalaryWarnLabel.Text = text;
    SalaryWarnLabel.Color = "#dc3545";
}

// その月に工数を入れているのに人件費コストが登録されていない社員の人数。
// 正典の定義は SQL ビュー `v_missing_salary`（ddl/760）。ここはその script 版で、
// **同じ粒度（人×月）**でなければならない。片方だけ緩めると警告が食い違う
int MissingSalaryUserCount(FiscalPeriod fp)
{
    var ts = new ModuleSearcher<TimeEntry>();
    ts.AddGreaterThanOrEqual(e => e.WorkDate.Value, fp.StartDate.Value);
    ts.AddLessThanOrEqual(e => e.WorkDate.Value, fp.EndDate.Value);
    var worked = new List<string>();
    foreach (var r in ts.Execute())
    {
        var t = (TimeEntry)r;
        if (t.UserRef.Value == null) continue;
        var key = $"{t.UserRef.Value}";
        if (!worked.Contains(key)) { worked.Add(key); }
    }
    if (worked.Count == 0) return 0;

    var ms = new ModuleSearcher<MonthlySalary>();
    ms.AddEquals(e => e.FiscalYearRef.Value, fp.FiscalYearId.Value);
    ms.AddEquals(e => e.PeriodNo.Value, fp.PeriodNo.Value);
    var registered = new List<string>();
    foreach (var r in ms.Execute())
    {
        registered.Add($"{((MonthlySalary)r).UserRef.Value}");
    }

    var lack = 0;
    foreach (var k in worked)
    {
        if (!registered.Contains(k)) { lack = lack + 1; }
    }
    return lack;
}
