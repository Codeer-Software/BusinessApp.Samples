// BudgetVsActual.mod.cs — 予実対比の既定検索（2026-08-06 ユーザー要望）
// 開いた時点で「現在の会計年度 ＋（非経理なら）自部門」で検索済みにする。
// ポータルの予算警告リンクは ?initialize_search=true 付きで遷移してくる（#48: 検索初期化の発火条件）。

void Search_OnInit()
{
    YearWarnLabel.Text = "";
    YearWarnLabel.Color = "";

    var today = DateOnly.FromDateTime(DateTime.Today);

    // **今日を含む年度**を現在年度とする（BUG-0435）。
    // ポータルの予算警告（`PortalAlertData.Query.sql` の `cur_yr`）と**同じ定義**に揃える。
    // 片方が「開始日が今日以前で最新」だと、翌年度を作り忘れた期間に
    // 「ポータルは 0 件と言うのに、画面を開くと前年度の警告が並ぶ」食い違いが起きる（ADR-0060）。
    // 黙って前年度に落とすのではなく、**年度が無いことを画面で言う**（BUG-0246 と同じ方針）
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, today);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, today);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy != null)
    {
        FiscalYearRef.SearchValue = ((FiscalYear)fy).Id.Value;
    }
    else
    {
        YearWarnLabel.Text = "⚠ 今日を含む会計年度がありません。"
            + "現在年度を決められないため、年度は未選択で開いています"
            + "（ポータルの予算警告も同じ理由で 0 件になります）。"
            + "業務マスタ > 会計年度 で次年度を作成してください";
        YearWarnLabel.Color = "#dc3545";
    }

    // 経理は全部門を横断して見る運用のため、部門の既定は非経理のみ
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        DepartmentRef.SearchValue = CurrentUser.所属部.Value;
    }
}
