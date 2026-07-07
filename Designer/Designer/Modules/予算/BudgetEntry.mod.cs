// BudgetEntry.mod.cs — 予算一括登録（表示専用モジュール・経理ロール専用）
// 責務: 年度×部門×科目の年間予算を 12 ヶ月均等で budget_lines に展開する
// 月割 = 年間額/12 切り捨て、端数は period_no=12（期末月）に加算
// 既存行は period_no で突き合わせて上書き（検索インスタンス直接 Submit = B4-4 実証済みパターン）、
// 不足 period のみ新規作成。ループ内 Submit の N+1 は 12 件固定のため許容。

void Run_OnClick()
{
    if (CurrentUser.Role.Value != "accounting")
    {
        Toaster.Error("予算の登録は経理ロールのみ実行できます");
        return;
    }
    if (FiscalYearRef.Value == null) { Toaster.Error("会計年度を選択してください"); return; }
    if (DepartmentRef.Value == null) { Toaster.Error("部門を選択してください"); return; }
    if (AccountRef.Value == null) { Toaster.Error("勘定科目を選択してください"); return; }
    if (AnnualAmount.Value == null || AnnualAmount.Value <= 0) { Toaster.Error("年間予算額を入力してください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    int annual = AnnualAmount.Value;
    int monthly = annual / 12;
    int lastMonth = monthly + (annual - monthly * 12);

    // 既存行の取得 (fy × dept × acct)
    var s = new ModuleSearcher<BudgetLine>();
    s.AddEquals(b => b.FiscalYearRef.Value, FiscalYearRef.Value);
    s.AddEquals(b => b.DepartmentRef.Value, DepartmentRef.Value);
    s.AddEquals(b => b.AccountRef.Value, AccountRef.Value);
    var existing = s.Execute();

    var updated = 0;
    var created = 0;
    var p = 1;
    while (p <= 12)
    {
        int amount = (p == 12) ? lastMonth : monthly;

        BudgetLine target = null;
        foreach (var row in existing)
        {
            var b = (BudgetLine)row;
            if (b.PeriodNo.Value != null && (int)b.PeriodNo.Value == p)
            {
                target = b;
                break;
            }
        }

        if (target != null)
        {
            target.Amount.Value = amount;
            var retU = target.Submit();
            if (retU != true)
            {
                Toaster.Error($"予算の更新に失敗しました（月No {p}）");
                return;
            }
            updated = updated + 1;
        }
        else
        {
            var bl = new BudgetLine();
            bl.FiscalYearRef.Value = FiscalYearRef.Value;
            bl.DepartmentRef.Value = DepartmentRef.Value;
            bl.AccountRef.Value = AccountRef.Value;
            bl.PeriodNo.Value = p;
            bl.Amount.Value = amount;
            var retC = bl.Submit();
            if (retC != true)
            {
                Toaster.Error($"予算の登録に失敗しました（月No {p}）");
                return;
            }
            created = created + 1;
        }
        p = p + 1;
    }

    ResultLabel.Text = $"{FiscalYearRef.DisplayText} {DepartmentRef.DisplayText} {AccountRef.DisplayText}: 12ヶ月分を登録しました（月額 {monthly:#,0} 円 / 期末月 {lastMonth:#,0} 円、新規 {created}・上書き {updated}）";
    Toaster.Success("予算を登録しました");
}
