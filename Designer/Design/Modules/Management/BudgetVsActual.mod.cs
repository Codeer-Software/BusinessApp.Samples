// BudgetVsActual.mod.cs — 予実対比の既定検索（2026-08-06 ユーザー要望）
// 開いた時点で「現在の会計年度 ＋（非経理なら）自部門」で検索済みにする。
// ポータルの予算警告リンクは ?initialize_search=true 付きで遷移してくる（#48: 検索初期化の発火条件）。
// FiscalYearLookup は StartDate しか持たないため、「開始日が今日以前で最新」の年度を現在年度とする。

void Search_OnInit()
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    object currentId = null;
    object currentStart = null;
    var fys = new ModuleSearcher<FiscalYearLookup>();
    var rows = fys.Execute();
    foreach (var r in rows)
    {
        var fy = (FiscalYearLookup)r;
        var start = fy.StartDate.Value;
        if (start == null) continue;
        if (start > today) continue;
        if (currentStart == null || start > currentStart)
        {
            currentStart = start;
            currentId = fy.Id.Value;
        }
    }
    if (currentId != null)
    {
        FiscalYearRef.SearchValue = currentId;
    }

    // 経理は全部門を横断して見る運用のため、部門の既定は非経理のみ
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        DepartmentRef.SearchValue = CurrentUser.所属部.Value;
    }
}
