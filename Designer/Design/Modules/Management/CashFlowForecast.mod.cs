// CashFlowForecast.mod.cs — 資金繰り予測（画面の注意書きだけを担う）
//
// 予測そのものは `CashFlowForecastData.Query.sql`（当月含む今後 4 ヶ月）。
// ここでやるのは 1 つだけ——**出金の人件費が信用できない月があることを画面に出す**（BUG-0346）。
//
// 出金の人件費は `monthly_salaries`（年度 × 月 × 社員）から拾う。登録が無ければ**静かに 0 円**になり、
// 期末資金が実態より多く見える（＝資金ショートも危険水域も鳴らない）。金額を勝手に補うことはできないので、
// 気づけるようにする。配賦画面の「⚠未配賦」と同じ考え方。
//
// 判定は「**その月の人件費が何人分登録されているか**」（BUG-0432）。
// 「1 行でもあれば登録済み」だと、社員 30 名中 1 名だけの月を「登録済み」と判定して見逃す。
//
// **配賦（案件損益・仕掛品）とは判定が違う**ので注意。あちらは `v_missing_salary`＝
// 「工数はあるのに人件費が無い**人×月**」で、工数が無ければ配賦する先も無いから警告不要。
// こちらは**出金**の話で、給与は工数の有無に関わらず払う。
// 工数を条件にすると、誰も工数を入れていない先の月で警告が消える（実測で気づいた）。
// 基準人数は「直近で登録がある月の人数」を使う——社員数をどこかに持っているわけではないので、
// 「先月は 9 人だったのに今月は 1 人」を異常として拾う形にする。

void Detail_OnAfterInit()
{
    SalaryWarnLabel.Text = "";
    SalaryWarnLabel.Color = "";

    var today = DateTime.Today;
    var baseWarn = CashBaseWarning();   // 期首資金の起点そのものが作れていないか（BUG-0246）
    var baseRows = ReferenceSalaryRowCount();   // 直近で登録がある月の人数（基準）
    var missing = "";       // 人件費が未登録／人数が足りない月
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

        var rows = SalaryRowCount(fp.FiscalYearId.Value, fp.PeriodNo.Value);
        if (rows >= baseRows) continue;
        if (missing != "") { missing = missing + "・"; }
        if (rows == 0) { missing = missing + $"{target:yyyy年M月}（未登録）"; }
        else { missing = missing + $"{target:yyyy年M月}（{baseRows} 名中 {rows} 名のみ）"; }
    }

    var text = baseWarn;
    if (missing != "")
    {
        if (text != "") { text = text + "  "; }
        text = text + $"⚠ 人件費コストの登録が足りない月があります（{missing}）。"
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

// 期首資金の**起点**が作れているか（BUG-0246）。作れていなければ理由を返す（作れていれば ""）。
//
// `cash_now` は「当年度の期首残高 ＋ 当年度の posted 仕訳」しか足さない。だから:
//   ① 今日がどの会計年度にも入らない（次年度の作り忘れ）→ 期首資金 0 円・未払金 0 円
//   ② 当年度の期首残高がまだ無い（前期の決算確定・繰越を走らせていない。**期首から 2〜3 ヶ月ふつうに続く**）
//      → 前期末の現預金がまるごと欠落し、期首資金が当年度の増減分だけ（多くはマイナス）になる
// どちらも例外にならず、4 行すべてが「⚠ 資金ショート」になる。
// **一度でも空振りすると、本当にショートする月が来てもアラートが信用されない。**
// 金額を勝手に補うことはできないので、「この数字は当てにならない」と先に言う。
// ポータル側も同じ判定で件数を出さないようにしてある（`PortalAlertData.cash_base_ok`・ADR-0060）
string CashBaseWarning()
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, today);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, today);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null)
    {
        return "⚠ 今日を含む会計年度がありません。**期首資金が 0 円として計算されている**ため、"
            + "以下の金額と警告は当てになりません。業務マスタ > 会計年度 で当年度を作成してください。  ";
    }
    var typedFy = (FiscalYear)fy;

    var obs = new ModuleSearcher<OpeningBalance>();
    obs.AddEquals(o => o.FiscalYearId.Value, typedFy.Id.Value);
    obs.Limit(1);
    if (obs.ExecuteFirstOrDefault() != null) return "";

    // 前期が無い（初年度）なら期首残高が無いのは正常
    var ps = new ModuleSearcher<FiscalYear>();
    ps.AddLessThan(e => e.EndDate.Value, typedFy.StartDate.Value);
    ps.Limit(1);
    if (ps.ExecuteFirstOrDefault() == null) return "";

    return $"⚠ {typedFy.Name.Value} の期首残高がまだありません（前期の翌期繰越が未実施）。"
        + "**前期末の現預金が期首資金に入っていない**ため、以下の金額と警告は当てになりません。"
        + "会計業務 > 設定 > 会計年度 で前期を開き「翌期繰越を実行」してください。  ";
}

// その年度×月に登録されている人件費コストの行数（＝人数）
int SalaryRowCount(object fiscalYearId, object periodNo)
{
    var ms = new ModuleSearcher<MonthlySalary>();
    ms.AddEquals(e => e.FiscalYearRef.Value, fiscalYearId);
    ms.AddEquals(e => e.PeriodNo.Value, periodNo);
    return ms.Execute().Count;
}

// 基準人数＝**直近で登録がある月の人数**。
// 社員数をどこかに持っているわけではないので、これを「本来あるべき人数」の代わりに使う。
// 登録が 1 件も無ければ 0 を返し、その場合は警告を出さない
// （まだ一度も人件費を登録していない導入直後に、毎月 4 行の警告を出しても意味が無い）
int ReferenceSalaryRowCount()
{
    var ms = new ModuleSearcher<MonthlySalary>();
    // **2 本目は ThenByDescending**（BUG-0167 の同型）。CLB の `OrderBy` / `OrderByDescending` は内部で
    // `SortConditions.Clear()` を呼んでから積むので、**2 回書くと 1 本目が黙って捨てられる**。
    // 旧実装は年度の並びが消えて**期だけの降順**だった。年度をまたいだ瞬間に
    // 「古い年度の第 12 期」が最新扱いになり、人件費の未登録警告が誤る
    ms.OrderByDescending(e => e.FiscalYearRef.Value);
    ms.ThenByDescending(e => e.PeriodNo.Value);
    ms.Limit(1);
    var last = ms.ExecuteFirstOrDefault();
    if (last == null) return 0;
    var typed = (MonthlySalary)last;
    return SalaryRowCount(typed.FiscalYearRef.Value, typed.PeriodNo.Value);
}
