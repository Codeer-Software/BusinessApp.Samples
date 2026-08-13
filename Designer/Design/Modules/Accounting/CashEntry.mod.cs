// CashEntry.mod.cs — 入出金起票（表示専用モジュール・経理専用）
// 責務: 現預金の入出金を仕訳で起票する。相手科目が課税区分なら金額を税込として扱い、
//        消費税行まで生成する（ADR-0053。それ以外は 2 行仕訳）
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
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("入出金の起票は経理のみ実行できます");
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
    // 相手科目そのものが取引の経済的実体なので、勘定科目マスタの既定税区分を明示的に入れる
    // （現預金側は対象外のまま）。一律に対象外へ倒すと受取利息のような非課税売上を取りこぼし、
    // 課税売上割合の分母が狂う（ADR-0052）。
    // 相手科目が課税区分なら**内税**として扱い、確定前に消費税行を生成する（ADR-0053・改善候補 B-7）。
    // 金額は税込で入力される前提。外税は使わない——外税だと本体行が税込のまま税行が増えて貸借が崩れる。
    var counterS = new ModuleSearcher<Account>();
    counterS.AddEquals(e => e.Id.Value, CounterAccount.Value);
    var counterAcc = counterS.ExecuteFirstOrDefault();
    var isTaxable = false;
    if (counterAcc != null)
    {
        var counterTaxCat = ((Account)counterAcc).DefaultTaxCategory.Value;
        if (counterTaxCat != null)
        {
            var tcS = new ModuleSearcher<TaxCategory>();
            tcS.AddEquals(e => e.Id.Value, counterTaxCat);
            var tcm = tcS.ExecuteFirstOrDefault();
            if (tcm != null)
            {
                var taxType = ((TaxCategory)tcm).TaxationType.Value;
                if (taxType == "taxable_sales" || taxType == "taxable_purchase") { isTaxable = true; }
            }
        }
        foreach (var row in je.Lines.Rows)
        {
            var l = (JournalLine)row;
            if ($"{l.Account.Value}" != $"{CounterAccount.Value}") continue;
            l.TaxCategory.Value = counterTaxCat;
            if (isTaxable) { l.TaxInputMode.Value = "inclusive"; }
        }
    }

    je.MarkRemainingLinesOutOfScope();

    // 税行の生成は入力額（税込）のまま 1 回だけ。この画面は下書きを経ずに確定するので順路は 1 本。
    // 税額は Submit の前に素の int に取り出しておく（保存後に動的値へ書式指定を掛けると空になる）。
    var taxAmount = 0;
    if (isTaxable)
    {
        je.GenerateTaxLinesOnce();
        foreach (var row in je.Lines.Rows)
        {
            var l = (JournalLine)row;
            if (l.IsTaxLine.Value == true) { taxAmount = l.Amount.Value ?? 0; }
        }
    }

    var ret = je.Submit();
    if (ret != true) { Toaster.Error("仕訳の起票に失敗しました"); return; }

    // 税を分けたときは結果に明示する（費用の金額が入力額より減るのは利用者にとって驚きになるため）
    var taxNote = "";
    if (taxAmount > 0) { taxNote = $"／うち消費税 {taxAmount:#,0} 円"; }
    ResultLabel.Text = $"仕訳 No.{nextNo} を起票しました（{desc} {amount:#,0} 円{taxNote}）";
    Amount.Value = null;
    Description.Value = null;
    Toaster.Success($"仕訳 No.{nextNo} を起票しました{taxNote}");
}
