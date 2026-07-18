// Receipt.mod.cs — 入金
// 責務: 請求書選択時の残額自動セット / 入金確定→消込仕訳 (D 普通預金1020 / C 売掛金1100、経理ロール専用) /
//        請求書ステータスの更新 (入金合計 >= 請求税込額 → paid、それ以外 → partial) /
//        少額差額の自動処理 (差額が RECEIPT_DIFF_MAX 円以下なら振込手数料等として
//        支払手数料6210 で自動仕訳し paid にする。閾値は system_thresholds マスタ参照)
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
    if (CurrentUser.Role.Value != "accounting" && CurrentUser.Role.Value != "sysadmin")
    {
        Toaster.Error("入金の確定（消込）は経理ロールのみ実行できます");
        return;
    }
    if (InvoiceRef.Value == null) { Toaster.Error("請求書を選択してください"); return; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("入金額を入力してください"); return; }
    if (ReceiptDate.Value == null) { Toaster.Error("入金日を入力してください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 過入金ガードは保存より前に判定する（拒否された金額をレコードに残さない）
    var ivGuard = FindInvoice(InvoiceRef.Value);
    if (ivGuard == null) { Toaster.Error("請求書が見つかりません"); return; }
    int guardGross = (ivGuard.Amount.Value ?? 0) + (ivGuard.TaxAmount.Value ?? 0);
    int guardOthers = SumReceipts(InvoiceRef.Value, true);
    int guardRemain = guardGross - guardOthers;
    if (Amount.Value > guardRemain)
    {
        Toaster.Error($"入金額 {Amount.Value:#,0} 円が請求残額 {guardRemain:#,0} 円を超えています。過入金分は前受金(2100)として振替伝票で起票してください");
        return;
    }

    // 保存 (保存→確定を 1 ボタンで)。既存レコードでも金額等の修正を必ず反映する
    // （過入金で弾かれた後に金額を直して再確定すると、修正が保存されず
    //   仕訳と入金レコードの金額が食い違うバグがあった。Submit の null は変更なし=正常）
    if (this.ValidateInput() != true) { Toaster.Error("入力内容を確認してください"); return; }
    var retSave = this.Submit();
    if (retSave == false) { Toaster.Error("入金の保存に失敗しました"); return; }

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "receipt");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) { Toaster.Error("この入金の消込仕訳は既に生成済みです"); return; }

    // 会計年度の解決と締め済み期間ガード (境界日知見: 月末日は辞書順比較で失敗するため月初日で解決)
    var rcpMonthFirst = new DateOnly(ReceiptDate.Value.Year, ReceiptDate.Value.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, rcpMonthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, rcpMonthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("入金日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, rcpMonthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, rcpMonthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("入金日に対応する月次期間がありません"); return; }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("入金日の期間は締め済みです。仕訳は手動で起票してください"); return; }

    var iv = FindInvoice(InvoiceRef.Value);
    if (iv == null) { Toaster.Error("請求書が見つかりません"); return; }

    // 科目解決: 普通預金1020 / 売掛金1100 / 支払手数料6210 / 仮払消費税1900
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1020", "1100", "6210", "1900");
    var accounts = accS.Execute();
    object bankAccountId = null;
    object arAccountId = null;
    Account feeAccount = null;
    object purchaseTaxAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1020") { bankAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1100") { arAccountId = acc.Id.Value; }
        if (acc.Code.Value == "6210") { feeAccount = acc; }
        if (acc.Code.Value == "1900") { purchaseTaxAccountId = acc.Id.Value; }
    }
    if (bankAccountId == null) { Toaster.Error("普通預金(1020)の科目がありません"); return; }
    if (arAccountId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }

    // 差額の判定: 入金前の請求残額 − 入金額 が 1〜閾値 なら振込手数料等として自動処理
    int grossAll = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
    int receivedOthers = SumReceipts(InvoiceRef.Value, true);
    int remainBefore = grossAll - receivedOthers;
    int inputAmount = Amount.Value;

    // 過入金ガード: 売掛金がマイナス残高になる消込は起票しない（誤入金・重複振込対策）
    if (inputAmount > remainBefore)
    {
        Toaster.Error($"入金額 {inputAmount:#,0} 円が請求残額 {remainBefore:#,0} 円を超えています。過入金分は前受金(2100)として振替伝票で起票してください");
        return;
    }

    var diff = remainBefore - inputAmount;
    var diffMax = GetThresholdAmount("RECEIPT_DIFF_MAX");
    var useDiff = (diff >= 1 && diff <= diffMax && feeAccount != null);

    // 差額の内税分解（支払手数料の既定税区分が課税仕入のとき）
    var diffTax = 0;
    object diffTaxCatId = null;
    if (useDiff)
    {
        diffTaxCatId = feeAccount.DefaultTaxCategory.Value;
        if (diffTaxCatId != null && purchaseTaxAccountId != null)
        {
            var cs = new ModuleSearcher<TaxCategory>();
            cs.AddEquals(c => c.Id.Value, diffTaxCatId);
            var foundCat = cs.ExecuteFirstOrDefault();
            if (foundCat != null)
            {
                var tcat = (TaxCategory)foundCat;
                if (tcat.TaxationType.Value == "taxable_purchase" && tcat.Rate.Value != null)
                {
                    var rs = new ModuleSearcher<TaxRate>();
                    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
                    var foundRate = rs.ExecuteFirstOrDefault();
                    if (foundRate != null)
                    {
                        decimal pct = ((TaxRate)foundRate).RatePercent.Value ?? 0;
                        if (pct > 0) { diffTax = diff * pct / (100 + pct); }
                    }
                }
            }
        }
    }

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
    var projId = iv.ProjectRef.Value;  // 請求書の案件を消込仕訳の全行に引き継ぐ（案件別元帳・案件損益のトレーサビリティ）

    // 消込仕訳: D 普通預金(入金額) [+ D 支払手数料(差額本体) + D 仮払消費税(差額税)] / C 売掛金(請求残額)
    var lineCount = 2;
    if (useDiff) { lineCount = (diffTax > 0) ? 4 : 3; }
    var creditAmount = useDiff ? remainBefore : amount;
    var je = new JournalEntry();
    je.EntryDate.Value = ReceiptDate.Value;
    je.EntryType.Value = "auto";
    je.Description.Value = $"入金 {invoiceNo}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "receipt";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(lineCount);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = $"入金 {invoiceNo}";
        l.TaxInputMode.Value = "none";
        if (projId != null) { l.ProjectRef.Value = projId; }
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = bankAccountId;
            l.Amount.Value = amount;
            l.InputAmount.Value = amount;
        }
        else if (useDiff && idx == 2)
        {
            l.Dc.Value = "D";
            l.Account.Value = feeAccount.Id.Value;
            if (diffTaxCatId != null) { l.TaxCategory.Value = diffTaxCatId; }
            l.TaxInputMode.Value = (diffTax > 0) ? "inclusive" : "none";
            l.Amount.Value = diff - diffTax;
            l.InputAmount.Value = diff;
            l.Description.Value = $"振込手数料等の差額（{invoiceNo}）";
        }
        else if (useDiff && idx == 3 && diffTax > 0)
        {
            l.Dc.Value = "D";
            l.Account.Value = purchaseTaxAccountId;
            l.TaxCategory.Value = diffTaxCatId;
            l.IsTaxLine.Value = true;
            l.ParentLineNo.Value = 2;
            l.Amount.Value = diffTax;
            l.InputAmount.Value = diffTax;
            l.Description.Value = "消費税（行2）";
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = arAccountId;
            l.Amount.Value = creditAmount;
            l.InputAmount.Value = creditAmount;
        }
    }
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("消込仕訳の生成に失敗しました"); return; }

    // 請求書ステータス更新: 差額自動処理なら paid / それ以外は 入金合計 >= 税込請求額 で判定
    int received = SumReceipts(InvoiceRef.Value, false);
    var newStatus = "partial";
    var newStatusText = "一部入金";
    if (useDiff || received >= grossAll)
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
    if (useDiff)
    {
        Toaster.Success($"仕訳 No.{nextNo}: 入金 {amount:#,0} 円＋差額 {diff:#,0} 円を支払手数料で処理し、{invoiceNo} を消し込みました（入金済）");
    }
    else
    {
        Toaster.Success($"仕訳 No.{nextNo}: 入金 {amount:#,0} 円を消し込みました（{invoiceNo} は{newStatusText}）");
    }
}

// system_thresholds から指定コードの閾値を期間解決して取得（該当なしは 0。ExpenseRequest と同型）
int GetThresholdAmount(string code)
{
    var s = new ModuleSearcher<SystemThreshold>();
    var thresholds = s.Execute();
    var d = ReceiptDate.Value;
    var limit = 0;
    foreach (var t in thresholds)
    {
        var th = (SystemThreshold)t;
        if (th.Code.Value != code) continue;
        if (d != null && th.ValidFrom.Value != null && d < th.ValidFrom.Value) continue;
        if (d != null && th.ValidTo.Value != null && d > th.ValidTo.Value) continue;
        limit = th.Amount.Value ?? 0;
    }
    return limit;
}
