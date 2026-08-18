// Receipt.mod.cs — 入金
// 責務: 請求書選択時の残額自動セット / 入金確定→消込仕訳 (経理専用) /
//        請求書ステータスの更新 (入金合計 >= 請求税込額 → paid、それ以外 → partial) /
//        少額差額の自動処理 (bank のみ。差額が RECEIPT_DIFF_MAX 円以下なら振込手数料等として
//        支払手数料6210 で自動仕訳し paid にする。閾値は system_thresholds マスタ参照)
// 消込仕訳の借方は入金方法で切替 (ADR-0035): bank→普通預金1020 / cash→現金1000 / offset→買掛金2000。
// 相殺 (offset) は同一取引先の仕入先請求 (未払計上済み) を全額消込し、買掛側も paid に連動させる。
// 仕訳生成の正典: ExpenseRequest.GenerateJournal_OnClick / Acceptance.Confirm_OnClick
//
// **請求書（InvoiceRef）は読み取り専用**（BUG-0299）。入金レコードの正体は請求書の発行時・
// 一部入金の確定時に自動作成される「入金予定」であり、請求書と 1 対 1 で対応している
// （ADR-0032／手動新規作成は ADR-0033 で廃止済み＝この画面に「手で作った入金」は存在しない）。
// 付け替えられると ①元の請求書が入金予定を失って回収漏れの検知から消え ②付け替え先が
// 入金予定 2 件になり ③資金繰り予測の入金見込みが両方向に狂い ④取引先すら跨げてしまう。
// 直したいときは「入金を取り消す」→ 請求書側で下書きに戻す／取消にする、で作り直す。
// レイアウトの IsViewOnly（新規・更新の両方に効く）＋ IsUpdateProtected（サーバ側の保険）の
// 二段構えにするのは ADR-0062 の規約どおり。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        // 入金の手動新規作成は廃止（ADR-0033）。入金予定は請求書の発行時・一部入金の確定時に
        // 自動作成される——URL 直打ち等で新規画面に来た場合は一覧へ戻す
        Toaster.Info("入金は請求書の発行時に自動作成されます（入金一覧の「未確定」から確定してください）");
        NavigationService.NavigateTo(NavigationService.GetModuleUrl("ReceiptBoard"));
        return;
    }
    UpdateButtons();
    UpdateOffsetVisibility();
}

// 「相殺する仕入請求」は入金方法が相殺のときだけ表示する
void UpdateOffsetVisibility()
{
    var isOffset = (Method.Value == "offset");
    OffsetVendorInvoiceRefLabel.IsVisible = isOffset;
    OffsetVendorInvoiceRef.IsVisible = isOffset;
}

void Method_OnDataChanged()
{
    UpdateOffsetVisibility();
}

// 仕入請求の選択で入金額を税込額に自動セット（相殺は仕入請求の全額消込のみ対応・ADR-0035）
void OffsetVendorInvoiceRef_OnDataChanged()
{
    if (OffsetVendorInvoiceRef.Value == null) return;
    var vi = FindVendorInvoice(OffsetVendorInvoiceRef.Value);
    if (vi == null) return;
    if (vi.Amount.Value != null) { Amount.Value = vi.Amount.Value; }
}

VendorInvoice FindVendorInvoice(object viId)
{
    var s = new ModuleSearcher<VendorInvoice>();
    s.AddEquals(e => e.Id.Value, viId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (VendorInvoice)found;
}

// 確定済み (消込仕訳が存在) なら閲覧専用＋取消ボタン、未確定なら確定ボタン
// （未確定の削除ボタンは廃止・ADR-0033。予定の削除は請求書側の巻き戻し/取消が自動で行う）
void UpdateButtons()
{
    var confirmed = (FindReceiptJournal() != null);
    this.IsViewOnly = confirmed;
    // CLB 1.3: モジュール全体を閲覧専用にするとボタンの OnClick も発火しなくなるため、
    // 確定後も操作する取消ボタンだけ個別に閲覧専用を解除する
    if (confirmed) { CancelReceiptButton.IsViewOnly = false; }
    ConfirmButton.IsVisible = !confirmed;
    CancelReceiptButton.IsVisible = confirmed && CurrentUser.HasAccountingAccess.Value == true;
    SubmitButton.IsVisible = !confirmed;
}

// 仕訳を明細→親の順に物理削除する。子持ちモジュールの検索インスタンス Delete() は
// 親単独では静かに失敗する（実測）ため、行ごとに削除し全戻り値を検証する
bool DeleteJournalEntryWithLines(JournalEntry je)
{
    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(l => l.JournalEntryId.Value, je.Id.Value);
    var lines = ls.Execute();
    foreach (var row in lines)
    {
        var l = (JournalLine)row;
        var okLine = l.Delete();
        if (okLine != true) { return false; }
    }
    var ok = je.Delete();
    if (ok != true) { return false; }
    return true;
}

// この入金の消込仕訳（無ければ null）
JournalEntry FindReceiptJournal()
{
    if (this.IsNewData) return null;
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "receipt");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var found = js.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (JournalEntry)found;
}

// 入金の取り消し: 消込仕訳を削除し、請求書ステータスを再計算する（経理専用）
void CancelReceipt_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("入金の取り消しは経理のみ実行できます");
        return;
    }
    var je = FindReceiptJournal();
    if (je == null) { Toaster.Error("この入金の消込仕訳が見つかりません"); return; }

    // 仕訳日の期間が締め済みなら削除しない
    var d = je.EntryDate.Value;
    if (d != null)
    {
        var monthFirst = new DateOnly(d.Year, d.Month, 1);
        var ps = new ModuleSearcher<FiscalPeriod>();
        ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
        ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
        var period = ps.ExecuteFirstOrDefault();
        if (period != null && ((FiscalPeriod)period).Status.Value == "closed")
        {
            Toaster.Error("消込仕訳の期間が締め済みのため取り消せません（決算修正仕訳（赤伝）で対応してください）");
            return;
        }
    }

    var jeNo = je.JournalNo.Value;
    var confirmMsg = $"消込仕訳 No.{jeNo} を削除して入金を未確定に戻します。よろしいですか？";
    if (Method.Value == "offset" && OffsetVendorInvoiceRef.Value != null)
    {
        confirmMsg = $"消込仕訳 No.{jeNo} を削除して入金を未確定に戻します（相殺した仕入先請求も未払計上済みに戻ります）。よろしいですか？";
    }
    var result = MessageBox.Show(confirmMsg, "取り消す", "キャンセル");
    if (result != "取り消す") return;

    using var loading = LoadingService.StartLoading(0);

    // 相殺の巻き戻し（ADR-0035）: vendor_invoices.payment_entry_id が消込仕訳を FK 参照しているため、
    // 参照解除（accrued へ戻す）→ 仕訳削除 の順序が必須。巻き戻しに失敗したら取消自体を中止する
    if (Method.Value == "offset" && OffsetVendorInvoiceRef.Value != null)
    {
        var vi = FindVendorInvoice(OffsetVendorInvoiceRef.Value);
        if (vi != null && vi.PaymentEntryId.Value != null && $"{vi.PaymentEntryId.Value}" == $"{je.Id.Value}")
        {
            vi.PaymentEntryId.Value = null;
            vi.PaidDate.Value = null;
            vi.Status.Value = "accrued";
            var retVi = vi.Submit();
            if (retVi == false)
            {
                Toaster.Error("仕入先請求の巻き戻しに失敗したため取り消せません（入金は確定済みのままです）");
                return;
            }
        }
    }

    if (!DeleteJournalEntryWithLines(je))
    {
        Toaster.Error("消込仕訳の削除に失敗しました（入金は確定済みのままです）");
        return;
    }

    // 請求書ステータスの再計算: 消込仕訳が残っている入金の合計で判定
    var mergedRemaining = 0;
    if (InvoiceRef.Value != null)
    {
        var iv = FindInvoice(InvoiceRef.Value);
        if (iv != null)
        {
            int gross = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
            var rs = new ModuleSearcher<Receipt>();
            rs.AddEquals(e => e.InvoiceRef.Value, InvoiceRef.Value);
            var rows = rs.Execute();
            var confirmedTotal = 0;
            foreach (var row in rows)
            {
                var r = (Receipt)row;
                var js2 = new ModuleSearcher<JournalEntry>();
                js2.AddEquals(e => e.SourceType.Value, "receipt");
                js2.AddEquals(e => e.SourceId.Value, r.Id.Value);
                if (js2.Execute().Count == 0) continue;
                if (r.Amount.Value != null) { confirmedTotal = confirmedTotal + r.Amount.Value; }
            }
            var newStatus = "issued";
            if (confirmedTotal >= gross && gross > 0) { newStatus = "paid"; }
            else if (confirmedTotal > 0) { newStatus = "partial"; }
            iv.Status.Value = newStatus;
            var retInv = iv.Submit();
            if (retInv != true) { Toaster.Error("請求書ステータスの更新に失敗しました（仕訳は削除済みです）"); }

            // 入金予定の統合（ADR-0033 追補・2026-07-26）: 取消で未確定に戻った本人の行を
            // 「残額の入金予定」に更新し、他の未確定予定（一部入金の確定時に自動作成した残額予定など）は
            // 削除する。確定・取消をどの順で繰り返しても「未確定はちょうど1行＝残額」に収束させる
            foreach (var row in rows)
            {
                var r = (Receipt)row;
                if (r.Id.Value == this.Id.Value) continue;
                var js3 = new ModuleSearcher<JournalEntry>();
                js3.AddEquals(e => e.SourceType.Value, "receipt");
                js3.AddEquals(e => e.SourceId.Value, r.Id.Value);
                if (js3.Execute().Count > 0) continue;
                var okDel = r.Delete();
                if (okDel != true) { Toaster.Warn("他の未確定の入金予定の削除に失敗しました（入金一覧を確認してください）"); }
            }
            var remaining = gross - confirmedTotal;
            if (remaining > 0)
            {
                this.IsViewOnly = false;  // 確定中ロックの解除（UpdateButtons が最終状態を再設定する）
                Amount.Value = remaining;
                // 入金方法も既定（銀行振込）へ戻す（BUG-0064）。相殺入金を取り消したときに
                // `method='offset'` を残すと、相殺先を消したのに相殺のまま＝**相殺先が無い相殺**という
                // 自己矛盾した行になる（不変条件 C05 が実データで検出した）。
                // 残額の予定は「これから普通に入金される見込み」なので、既定に戻すのが素直
                Method.Value = "bank";
                OffsetVendorInvoiceRef.Value = null;
                Note.Value = "入金の取消により残額の入金予定に戻りました（入金日・金額・入金方法を実額に修正して確定してください）";
                var retSelf = this.Submit();
                if (retSelf != true) { Toaster.Warn("入金予定の金額更新に失敗しました（金額を確認してください）"); }
                else { mergedRemaining = remaining; }
            }
        }
    }

    UpdateButtons();
    if (mergedRemaining > 0)
    {
        Toaster.Success($"仕訳 No.{jeNo} を削除し、入金予定を残額 {mergedRemaining:#,0} 円に統合しました（入金日・金額を実額に修正して確定してください）");
    }
    else
    {
        Toaster.Success($"仕訳 No.{jeNo} を削除し、入金を未確定に戻しました");
    }
}

Invoice FindInvoice(object invoiceId)
{
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.Id.Value, invoiceId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (Invoice)found;
}

// 同一請求書への入金合計。excludeSelf=true なら自分の保存済み行を除外。
// 消込済み（消込仕訳あり）のみ数える——未確定は「入金予定」であってまだ入金ではない
// （請求書発行時の入金予定の自動作成に伴い変更・2026-07-25。含めると残額計算・過入金ガードが狂う）
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
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, r.Id.Value);
        if (js.Execute().Count == 0) continue;
        if (r.Amount.Value != null) total = total + r.Amount.Value;
    }
    return total;
}

// 入金確定: 保存 → 消込仕訳 → 請求書ステータス更新 (経理専用)
void Confirm_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("入金の確定（消込）は経理のみ実行できます");
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

    // 相殺の検証（ADR-0035）: 仕入請求の指定・状態・取引先一致・全額一致
    VendorInvoice offsetVi = null;
    if (Method.Value == "offset")
    {
        if (OffsetVendorInvoiceRef.Value == null) { Toaster.Error("相殺する仕入請求を選択してください"); return; }
        offsetVi = FindVendorInvoice(OffsetVendorInvoiceRef.Value);
        if (offsetVi == null) { Toaster.Error("選択した仕入請求が見つかりません"); return; }
        if (offsetVi.Status.Value == "received")
        {
            Toaster.Error("選択した仕入請求は未払計上前です（買掛金が立っていないため相殺できません。購買＞仕入先請求で先に未払計上してください）");
            return;
        }
        if (offsetVi.Status.Value != "accrued")
        {
            Toaster.Error("選択した仕入請求は既に支払済み（または相殺済み）です");
            return;
        }
        if (offsetVi.Partner.Value == null || ivGuard.PartnerRef.Value == null || $"{offsetVi.Partner.Value}" != $"{ivGuard.PartnerRef.Value}")
        {
            Toaster.Error("相殺は同一取引先の仕入請求とのみ可能です（請求書の取引先と仕入請求の取引先が一致しません）");
            return;
        }
        int viGross = offsetVi.Amount.Value ?? 0;
        if (viGross != Amount.Value)
        {
            Toaster.Error($"相殺は仕入請求の全額消込のみ対応しています。入金額を仕入請求の税込額 {viGross:#,0} 円に合わせてください（売掛の残りは残額の入金予定として自動作成されます）");
            return;
        }
    }
    else if (OffsetVendorInvoiceRef.Value != null)
    {
        // 相殺以外に切り替えた場合は選択済みの仕入請求をクリアする（取消時の巻き戻し誤爆防止）
        OffsetVendorInvoiceRef.Value = null;
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

    // 科目解決: 普通預金1020 / 現金1000 / 買掛金2000 / 売掛金1100 / 支払手数料6210 / 仮払消費税1900
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1020", "1000", "2000", "1100", "6210", "1900");
    var accounts = accS.Execute();
    object bankAccountId = null;
    object cashAccountId = null;
    object apAccountId = null;
    object arAccountId = null;
    Account feeAccount = null;
    object purchaseTaxAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1020") { bankAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1000") { cashAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2000") { apAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1100") { arAccountId = acc.Id.Value; }
        if (acc.Code.Value == "6210") { feeAccount = acc; }
        if (acc.Code.Value == "1900") { purchaseTaxAccountId = acc.Id.Value; }
    }
    if (arAccountId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }

    // 借方科目は入金方法で切替（ADR-0035）: bank→普通預金 / cash→現金 / offset→買掛金
    object debitAccountId = bankAccountId;
    if (Method.Value == "cash") { debitAccountId = cashAccountId; }
    if (Method.Value == "offset") { debitAccountId = apAccountId; }
    if (debitAccountId == null)
    {
        if (Method.Value == "cash") { Toaster.Error("現金(1000)の科目がありません"); }
        else if (Method.Value == "offset") { Toaster.Error("買掛金(2000)の科目がありません"); }
        else { Toaster.Error("普通預金(1020)の科目がありません"); }
        return;
    }

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
    // 「振込手数料等」の差額自動処理は銀行振込のみ（現金・相殺の不足額は振込手数料ではない・ADR-0035）
    var useDiff = (Method.Value == "bank" && diff >= 1 && diff <= diffMax && feeAccount != null);

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

    // 伝票採番（正典: JournalEntry.NextJournalNo。BUG-0069 で一本化）
    var nextNo = new JournalEntry().NextJournalNo(typedFy.Id.Value);

    int amount = Amount.Value;
    var invoiceNo = iv.InvoiceNo.Value;
    var projId = iv.ProjectRef.Value;  // 請求書の案件を消込仕訳の全行に引き継ぐ（案件別元帳・案件損益のトレーサビリティ）
    var entryDesc = $"入金 {invoiceNo}";
    if (offsetVi != null) { entryDesc = $"相殺入金 {invoiceNo}（仕入先請求 {offsetVi.InvoiceNo.Value}）"; }

    // 消込仕訳: D 借方科目=入金方法で切替(入金額) [+ D 支払手数料(差額本体) + D 仮払消費税(差額税)] / C 売掛金(請求残額)
    var lineCount = 2;
    if (useDiff) { lineCount = (diffTax > 0) ? 4 : 3; }
    var creditAmount = useDiff ? remainBefore : amount;
    var je = new JournalEntry();
    je.EntryDate.Value = ReceiptDate.Value;
    je.EntryType.Value = "auto";
    je.Description.Value = entryDesc;
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
        l.Description.Value = entryDesc;
        l.TaxInputMode.Value = "none";
        if (projId != null) { l.ProjectRef.Value = projId; }
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = debitAccountId;
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
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("消込仕訳の生成に失敗しました。ほかの人が同時に伝票を確定した可能性があります。もう一度お試しください"); return; }

    // 相殺（ADR-0035）: 仕入先請求を支払済みに連動させる（payment_entry_id=消込仕訳・paid_date=入金日）。
    // 消込仕訳 1 本が売掛・買掛両方の裏付けになる。取消は入金側から（買掛側の支払取消はブロック）
    if (offsetVi != null)
    {
        var cjs = new ModuleSearcher<JournalEntry>();
        cjs.AddEquals(e => e.SourceType.Value, "receipt");
        cjs.AddEquals(e => e.SourceId.Value, this.Id.Value);
        var createdJe = cjs.ExecuteFirstOrDefault();
        if (createdJe != null) { offsetVi.PaymentEntryId.Value = ((JournalEntry)createdJe).Id.Value; }
        offsetVi.PaidDate.Value = ReceiptDate.Value;
        offsetVi.Status.Value = "paid";
        var retVi = offsetVi.Submit();
        if (retVi != true)
        {
            Toaster.Error("仕入先請求の支払済み更新に失敗しました（消込仕訳は生成済みです。購買＞仕入先請求の状態を確認してください）");
        }
    }

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

    // 一部入金なら残額分の入金予定を自動で作り直す（分割入金の2回目以降・ADR-0033。
    // 手動新規作成の廃止とセットで「未回収の発行済み請求書には常に残額分の予定が1件ある」を保つ）
    if (newStatus == "partial")
    {
        var remainAfter = grossAll - received;
        if (remainAfter > 0) { CreateRemainderPendingReceipt(remainAfter, iv); }
    }

    UpdateButtons();
    if (useDiff)
    {
        Toaster.Success($"仕訳 No.{nextNo}: 入金 {amount:#,0} 円＋差額 {diff:#,0} 円を支払手数料で処理し、{invoiceNo} を消し込みました（入金済）");
    }
    else if (offsetVi != null)
    {
        Toaster.Success($"仕訳 No.{nextNo}: 買掛金 {amount:#,0} 円と相殺して消し込みました（{invoiceNo} は{newStatusText}／仕入先請求 {offsetVi.InvoiceNo.Value} は支払済）");
    }
    else
    {
        Toaster.Success($"仕訳 No.{nextNo}: 入金 {amount:#,0} 円を消し込みました（{invoiceNo} は{newStatusText}）");
    }
}

// 残額分の未確定入金（入金予定）を作成する。未確定行がまだ残っている場合は作らない（二重予定の防止）
void CreateRemainderPendingReceipt(int remainAmount, Invoice iv)
{
    var s = new ModuleSearcher<Receipt>();
    s.AddEquals(e => e.InvoiceRef.Value, iv.Id.Value);
    var rows = s.Execute();
    foreach (var row in rows)
    {
        var r = (Receipt)row;
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, r.Id.Value);
        if (js.Execute().Count == 0) { return; }
    }
    var nr = new Receipt();
    nr.InvoiceRef.Value = iv.Id.Value;
    nr.ReceiptDate.Value = iv.DueDate.Value;
    nr.Method.Value = "bank";
    nr.Amount.Value = remainAmount;
    nr.Note.Value = "一部入金の確定時に自動作成された残額分の入金予定です（入金日・金額を実額に修正して確定してください）";
    var okNr = nr.Submit();
    if (okNr != true) { Toaster.Warn("残額分の入金予定の自動作成に失敗しました"); }
    else { Toaster.Info($"残額 {remainAmount:#,0} 円の入金予定を自動作成しました"); }
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
