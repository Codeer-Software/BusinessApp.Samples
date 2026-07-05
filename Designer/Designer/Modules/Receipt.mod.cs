// Receipt.mod.cs — 入金
// 責務: 請求書選択時の残額自動セット / 入金確定→消込仕訳 (D 普通預金1020 / C 売掛金1100、経理ロール専用) /
//        請求書ステータスの更新 (入金合計 >= 請求税込額 → paid、それ以外 → partial)
// 仕訳生成の正典: ExpenseRequest.GenerateJournal_OnClick / Acceptance.Confirm_OnClick

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        ReceiptDate.Value = DateOnly.FromDateTime(DateTime.Today);
        Method.Value = "bank";
        ConfirmButton.IsVisible = true;
        return;
    }
    // 確定済み (消込仕訳が存在) なら閲覧専用
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "receipt");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0)
    {
        this.IsViewOnly = true;
        ConfirmButton.IsVisible = false;
    }
}

// 請求書選択: 請求税込額 − 既存入金合計 (自分以外) を入金額に自動セット (手修正可)
void InvoiceRef_OnDataChanged()
{
    if (InvoiceRef.Value == null) return;
    var iv = FindInvoice(InvoiceRef.Value);
    if (iv == null) return;
    int gross = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
    int received = SumReceipts(InvoiceRef.Value, true);
    var remain = gross - received;
    if (remain < 0) remain = 0;
    Amount.Value = remain;
}

Invoice FindInvoice(object invoiceId)
{
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.Id.Value, invoiceId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (Invoice)found;
}

// 同一請求書への入金合計。excludeSelf=true なら自分の保存済み行を除外
int SumReceipts(object invoiceId, bool excludeSelf)
{
    var s = new ModuleSearcher<Receipt>();
    s.AddEquals(e => e.InvoiceRef.Value, invoiceId);
    var rows = s.Execute();
    var total = 0;
    foreach (var row in rows)
    {
        var r = (Receipt)row;
        if (excludeSelf && !this.IsNewData && r.Id.Value == this.Id.Value) continue;
        if (r.Amount.Value != null) total = total + r.Amount.Value;
    }
    return total;
}

// 入金確定: 保存 → 消込仕訳 → 請求書ステータス更新 (経理ロール専用)
void Confirm_OnClick()
{
    if (CurrentUser.Role.Value != "accounting")
    {
        Toaster.Error("入金の確定（消込）は経理ロールのみ実行できます");
        return;
    }
    if (InvoiceRef.Value == null) { Toaster.Error("請求書を選択してください"); return; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("入金額を入力してください"); return; }
    if (ReceiptDate.Value == null) { Toaster.Error("入金日を入力してください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 未保存なら先に保存 (保存→確定を 1 ボタンで)
    if (this.IsNewData)
    {
        if (this.ValidateInput() != true) { Toaster.Error("入力内容を確認してください"); return; }
        var retSave = this.Submit();
        if (retSave != true) { Toaster.Error("入金の保存に失敗しました"); return; }
    }

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "receipt");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) { Toaster.Error("この入金の消込仕訳は既に生成済みです"); return; }

    // 会計年度の解決と締め済み期間ガード
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, ReceiptDate.Value);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, ReceiptDate.Value);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("入金日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, ReceiptDate.Value);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, ReceiptDate.Value);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("入金日に対応する月次期間がありません"); return; }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("入金日の期間は締め済みです。仕訳は手動で起票してください"); return; }

    var iv = FindInvoice(InvoiceRef.Value);
    if (iv == null) { Toaster.Error("請求書が見つかりません"); return; }

    // 科目解決: 普通預金1020 / 売掛金1100
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1020", "1100");
    var accounts = accS.Execute();
    object bankAccountId = null;
    object arAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1020") { bankAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1100") { arAccountId = acc.Id.Value; }
    }
    if (bankAccountId == null) { Toaster.Error("普通預金(1020)の科目がありません"); return; }
    if (arAccountId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }

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
    var invoiceNo = iv.InvoiceNo.Value;

    // 消込仕訳: D 普通預金 / C 売掛金
    var je = new JournalEntry();
    je.EntryDate.Value = ReceiptDate.Value;
    je.EntryType.Value = "auto";
    je.Description.Value = $"入金 {invoiceNo}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "receipt";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(2);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = $"入金 {invoiceNo}";
        l.TaxInputMode.Value = "none";
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = bankAccountId;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = arAccountId;
        }
    }
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("消込仕訳の生成に失敗しました"); return; }

    // 請求書ステータス更新: 入金合計 >= 税込請求額 → paid / それ以外 → partial
    int gross = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
    int received = SumReceipts(InvoiceRef.Value, false);
    var newStatus = "partial";
    var newStatusText = "一部入金";
    if (received >= gross)
    {
        newStatus = "paid";
        newStatusText = "入金済";
    }
    iv.Status.Value = newStatus;
    var retInv = iv.Submit();
    if (retInv != true)
    {
        Toaster.Error("請求書ステータスの更新に失敗しました（消込仕訳は生成済みです）");
    }

    this.IsViewOnly = true;
    ConfirmButton.IsVisible = false;
    Toaster.Success($"仕訳 No.{nextNo}: 入金 {amount:#,0} 円を消し込みました（{invoiceNo} は{newStatusText}）");
}
