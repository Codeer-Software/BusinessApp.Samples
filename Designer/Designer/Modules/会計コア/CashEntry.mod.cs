// CashEntry.mod.cs — 入出金起票（表示専用モジュール・経理ロール専用）
// 責務: 現預金の入出金を 2 行仕訳で起票する（税行なし。課税取引は振替伝票から）
// 入金: D 現預金 / C 相手科目、出金: D 相手科目 / C 現預金
// source_type='cashbook' で出所を記録（source_id は無し）

void Detail_OnAfterInit()
{
    if (EntryDate.Value == null)
    {
        EntryDate.Value = DateOnly.FromDateTime(DateTime.Today);
    }
    if (CashAccount.Value == null || CashAccount.Value == "")
    {
        CashAccount.Value = "1020";
    }
    if (Direction.Value == null || Direction.Value == "")
    {
        Direction.Value = "in";
    }
}

void Run_OnClick()
{
    if (CurrentUser.Role.Value != "accounting")
    {
        Toaster.Error("入出金の起票は経理ロールのみ実行できます");
        return;
    }
    if (EntryDate.Value == null) { Toaster.Error("日付を入力してください"); return; }
    if (CashAccount.Value == null || CashAccount.Value == "") { Toaster.Error("現預金科目を選択してください"); return; }
    if (Direction.Value == null || Direction.Value == "") { Toaster.Error("入出金を選択してください"); return; }
    if (CounterAccount.Value == null) { Toaster.Error("相手科目を選択してください"); return; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("金額を入力してください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var entryDate = EntryDate.Value;

    // 会計年度・期間の解決 (境界日知見: 期間解決はその月の月初日で行う)
    var monthFirst = new DateOnly(entryDate.Year, entryDate.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("日付に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("日付に対応する月次期間がありません"); return; }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("対象の期間は締め済みです"); return; }

    // 現預金科目をコードから解決
    var accS = new ModuleSearcher<Account>();
    accS.AddEquals(e => e.Code.Value, CashAccount.Value);
    var cashAcc = accS.ExecuteFirstOrDefault();
    if (cashAcc == null) { Toaster.Error($"現預金科目({CashAccount.Value})がありません"); return; }
    var cashAccountId = ((Account)cashAcc).Id.Value;

    // 伝票採番
    var ns = new ModuleSearcher<JournalEntry>();
    ns.AddEquals(e => e.FiscalYearRef.Value, typedFy.Id.Value);
    ns.OrderByDescending(e => e.JournalNo.Value);
    ns.Limit(1);
    var last = ns.ExecuteFirstOrDefault();
    var nextNo = 1;
    if (last != null)
    {
        var typedLast = (JournalEntry)last;
        if (typedLast.JournalNo.Value != null) { nextNo = (int)typedLast.JournalNo.Value + 1; }
    }

    int amount = Amount.Value;
    var isIn = (Direction.Value == "in");
    var desc = Description.Value;
    if (desc == null || desc == "") { desc = isIn ? "入金" : "出金"; }

    var je = new JournalEntry();
    je.EntryDate.Value = entryDate;
    je.EntryType.Value = "auto";
    je.Description.Value = desc;
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "cashbook";
    je.Lines.AddRows(2);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = desc;
        l.TaxInputMode.Value = "none";
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = isIn ? cashAccountId : CounterAccount.Value;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = isIn ? CounterAccount.Value : cashAccountId;
        }
    }
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("仕訳の起票に失敗しました"); return; }

    ResultLabel.Text = $"仕訳 No.{nextNo} を起票しました（{desc} {amount:#,0} 円）";
    Amount.Value = null;
    Description.Value = null;
    Toaster.Success($"仕訳 No.{nextNo} を起票しました");
}
