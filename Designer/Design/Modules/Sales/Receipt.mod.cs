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

    // 合算入金（ADR-0071）。**未確定のときだけ**まとめ直せる（確定後は取消が先）
    var isAccounting = (CurrentUser.HasAccountingAccess.Value == true);
    var lineCount = LineCount();
    var canMerge = !confirmed && isAccounting && !this.IsNewData && Method.Value != "offset";
    MergeButton.IsVisible = canMerge;
    if (canMerge) { MergeButton.IsViewOnly = false; }
    UnmergeButton.IsVisible = canMerge && lineCount > 1;
    if (UnmergeButton.IsVisible) { UnmergeButton.IsViewOnly = false; }
    // 合算したら入金額は明細の合計。手で直せると合計と食い違う（不変条件 C05）
    Amount.IsViewOnly = confirmed || lineCount > 1;
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
    if (LineCount() > 1) { CancelMulti(); return; }
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
            // 消込済みの合計は **SumReceipts（消込明細ベース）** で数える（ADR-0071）。
            // ヘッダの請求書欄で兄弟を探す旧実装だと、**合算された入金の充当分を取りこぼす**
            // （合算入金のヘッダは 1 件目の請求書しか指していない）。
            // ここは仕訳を消した直後なので、自分の分は自然に除外される
            var confirmedTotal = SumReceipts(InvoiceRef.Value, false);
            var newStatus = "issued";
            if (confirmedTotal >= gross && gross > 0) { newStatus = "paid"; }
            else if (confirmedTotal > 0) { newStatus = "partial"; }
            iv.Status.Value = newStatus;
            var retInv = iv.Submit();
            if (retInv != true) { Toaster.Error("請求書ステータスの更新に失敗しました（仕訳は削除済みです）"); }

            // 入金予定の統合（ADR-0033 追補・2026-07-26）: 取消で未確定に戻った本人の行を
            // 「残額の入金予定」に更新し、他の未確定予定（一部入金の確定時に自動作成した残額予定など）は
            // 削除する。確定・取消をどの順で繰り返しても「未確定はちょうど1行＝残額」に収束させる
            // 他の未確定予定の後始末。**合算入金を巻き添えにしない**（BUG-0379）——
            // ヘッダの請求書で兄弟を探すと、たまたまこの請求書を指している合算入金が丸ごと消え、
            // **他の請求書ぶんの消込明細まで道連れ**になる（警告も出ない）。
            // 明細がこの請求書 1 本だけの予定は削除し、合算入金は**その行だけ外して金額を減らす**
            // （`Invoice.DeletePendingReceipts` と同じ扱い）
            var rls2 = new ModuleSearcher<ReceiptLine>();
            rls2.AddEquals(l => l.InvoiceRef.Value, InvoiceRef.Value);
            foreach (var lrow in rls2.Execute())
            {
                var rl2 = (ReceiptLine)lrow;
                if ($"{rl2.ReceiptId.Value}" == $"{this.Id.Value}") continue;
                var js3 = new ModuleSearcher<JournalEntry>();
                js3.AddEquals(e => e.SourceType.Value, "receipt");
                js3.AddEquals(e => e.SourceId.Value, rl2.ReceiptId.Value);
                if (js3.Execute().Count > 0) continue;   // 消込済みは触らない

                var sib2 = new ModuleSearcher<ReceiptLine>();
                sib2.AddEquals(l => l.ReceiptId.Value, rl2.ReceiptId.Value);
                var sibCount2 = sib2.Execute().Count;

                var rs2 = new ModuleSearcher<Receipt>();
                rs2.AddEquals(e => e.Id.Value, rl2.ReceiptId.Value);
                var found2 = rs2.ExecuteFirstOrDefault();
                if (found2 == null) continue;
                var other = (Receipt)found2;
                if (sibCount2 <= 1)
                {
                    if (other.Delete() != true) { Toaster.Warn("他の未確定の入金予定の削除に失敗しました（入金一覧を確認してください）"); }
                    continue;
                }
                var rest2 = (other.Amount.Value ?? 0) - (rl2.Amount.Value ?? 0);
                if (rl2.Delete() != true) { Toaster.Warn("合算入金から消込明細を外せませんでした（入金一覧を確認してください）"); continue; }
                other.Amount.Value = rest2;
                if (other.Submit() == false) { Toaster.Warn("合算入金の金額更新に失敗しました（入金一覧を確認してください）"); }
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
    // **消込明細（receipt_lines）で数える。** 入金 1 件が複数の請求書に散るので（ADR-0071）、
    // `receipts.invoice_id` では数えられない。1 対 1 の入金も移行で明細 1 行を持っている
    var s = new ModuleSearcher<ReceiptLine>();
    s.AddEquals(l => l.InvoiceRef.Value, invoiceId);
    var total = 0;
    foreach (var row in s.Execute())
    {
        var rl = (ReceiptLine)row;
        if (excludeSelf && !this.IsNewData && $"{rl.ReceiptId.Value}" == $"{this.Id.Value}") continue;
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, rl.ReceiptId.Value);
        if (js.Execute().Count == 0) continue;
        if (rl.Amount.Value != null) total = total + rl.Amount.Value;
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
    // 合算入金（明細 2 行以上）は専用の経路へ。**既存の 1 対 1 の経路には手を触れない**
    // ——差額の自動処理・相殺・入金予定の統合はどれも 1 請求書を前提に作り込まれている（ADR-0071）
    if (LineCount() > 1) { ConfirmMulti(); return; }
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
    // 二重予定のガードは**消込明細**で見る（ADR-0071）。ヘッダで見ると、合算に取り込まれた
    // 予定を数え落として同じ請求書に予定が 2 本立つ（BUG-0380）
    var s = new ModuleSearcher<ReceiptLine>();
    s.AddEquals(l => l.InvoiceRef.Value, iv.Id.Value);
    var rows = s.Execute();
    foreach (var row in rows)
    {
        var rl = (ReceiptLine)row;
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, rl.ReceiptId.Value);
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

// ───────────────────────────────────────────────────────────────────────────
// 合算入金（ADR-0071・BUG-0012）
//
// 取引先は月末に複数の請求をまとめて 1 回で振り込んでくる。受託ソフトハウスでは日常的に起きるのに、
// 入金は請求書 1 本としか結び付けられなかった。銀行明細は 1 行なのに帳簿では n 行になり、
// 残高照合で必ず突き合わせに詰まる。
//
// **既存の 1:1 の経路には手を触れていない。** 明細が 1 行のときは従来どおりのコードが走る
// （差額の自動処理・相殺・入金予定の統合はどれも 1 請求書を前提に作り込まれており、
// そこを作り替える価値より壊す危険のほうが大きい）。明細が 2 行以上のときだけ、
// この下の専用の経路に分岐する。
//
// **まとめられるのは未確定（消込仕訳が無い）どうし・同じ取引先だけ。**
// 相殺（offset）は仕入先請求と 1:1 で対応する仕組みなので合算できない。
// ───────────────────────────────────────────────────────────────────────────

// この入金の消込明細（行番号順）
List<ReceiptLine> GetLines()
{
    var result = new List<ReceiptLine>();
    if (this.Id.Value == null) return result;
    var s = new ModuleSearcher<ReceiptLine>();
    s.AddEquals(l => l.ReceiptId.Value, this.Id.Value);
    s.OrderBy(l => l.LineNo.Value);
    foreach (var row in s.Execute()) { result.Add((ReceiptLine)row); }
    return result;
}

int LineCount()
{
    var n = 0;
    foreach (var l in GetLines()) { n = n + 1; }
    return n;
}

// 明細の合計。ヘッダの入金額はこれと一致していなければならない（不変条件 C05）
int LinesTotal()
{
    var total = 0;
    foreach (var l in GetLines())
    {
        if (l.Amount.Value != null) { total = total + l.Amount.Value; }
    }
    return total;
}

// この入金が充当している請求書の取引先（明細の 1 行目から引く）
object PartnerOfThisReceipt()
{
    foreach (var l in GetLines())
    {
        var iv = FindInvoice(l.InvoiceRef.Value);
        if (iv != null) return iv.PartnerRef.Value;
    }
    return null;
}

bool HasSettleJournal(object receiptId)
{
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "receipt");
    js.AddEquals(e => e.SourceId.Value, receiptId);
    return js.Execute().Count > 0;
}

// 明細を画面に映すための再読込（ボタン操作のあと）
void ReloadLines()
{
    Lines.Reload();
    UpdateButtons();
}

void Lines_OnDataChanged()
{
    // 明細はボタン操作でしか動かさない（グリッドは読み取り専用）。合計だけ追従させる
    var t = LinesTotal();
    if (t > 0 && LineCount() > 1) { Amount.Value = t; }
}

// 同じ取引先の未確定入金を、この入金にまとめる
void Merge_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("入金のまとめは経理のみ実行できます");
        return;
    }
    if (this.IsNewData) { Toaster.Error("入金を保存してから実行してください"); return; }
    if (HasSettleJournal(this.Id.Value))
    {
        Toaster.Error("確定済みの入金はまとめられません（先に「入金を取り消す」を実行してください）");
        return;
    }
    if (Method.Value == "offset")
    {
        Toaster.Error("相殺入金はまとめられません（相殺は仕入先請求と 1 対 1 で対応します・ADR-0035）");
        return;
    }
    var partner = PartnerOfThisReceipt();
    if (partner == null) { Toaster.Error("この入金の取引先が特定できません（消込明細がありません）"); return; }

    // 相手候補: 同じ取引先・未確定・相殺でない・自分以外
    var targets = new List<string>();
    var names = new List<string>();
    var amounts = new List<int>();
    var rs = new ModuleSearcher<Receipt>();
    foreach (var row in rs.Execute())
    {
        var r = (Receipt)row;
        if ($"{r.Id.Value}" == $"{this.Id.Value}") continue;
        if (r.Method.Value == "offset") continue;
        // 入金方法が違うものはまとめない（BUG-0382）。合算の借方は 1 行なので、
        // 現金の予定を銀行振込にまとめると**全額が普通預金**になってしまう
        if ($"{r.Method.Value}" != $"{Method.Value}") continue;
        if (HasSettleJournal(r.Id.Value)) continue;
        var ls = new ModuleSearcher<ReceiptLine>();
        ls.AddEquals(l => l.ReceiptId.Value, r.Id.Value);
        var rlines = ls.Execute();
        if (rlines.Count == 0) continue;
        var samePartner = true;
        var label = "";
        foreach (var lrow in rlines)
        {
            var rl = (ReceiptLine)lrow;
            var iv = FindInvoice(rl.InvoiceRef.Value);
            if (iv == null || $"{iv.PartnerRef.Value}" != $"{partner}") { samePartner = false; break; }
            if (label != "") { label = label + " / "; }
            label = label + $"{iv.InvoiceNo.Value} {rl.Amount.Value:#,0} 円";
        }
        if (!samePartner) continue;
        targets.Add($"{r.Id.Value}");
        names.Add(label);
        amounts.Add(r.Amount.Value ?? 0);
    }
    if (targets.Count == 0)
    {
        Toaster.Info("まとめられる入金がありません（同じ取引先の未確定の入金予定が他にありません）");
        return;
    }

    var listText = "";
    var addTotal = 0;
    var i = 0;
    foreach (var n in names)
    {
        listText = listText + "／" + n;
        addTotal = addTotal + amounts[i];
        i = i + 1;
    }
    var answer = MessageBox.Show(
        $"同じ取引先の未確定入金 {targets.Count} 件をこの入金にまとめます{listText}。"
        + $"入金額は {Amount.Value:#,0} 円 → {(Amount.Value ?? 0) + addTotal:#,0} 円になります。"
        + "まとめた入金予定の行は無くなります（「まとめを解除する」で元に戻せます）。よろしいですか？",
        "まとめる", "キャンセル");
    if (answer != "まとめる") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var nextNo = LineCount();
    foreach (var tid in targets)
    {
        var ls = new ModuleSearcher<ReceiptLine>();
        ls.AddEquals(l => l.ReceiptId.Value, tid);
        foreach (var lrow in ls.Execute())
        {
            var rl = (ReceiptLine)lrow;
            nextNo = nextNo + 1;
            rl.ReceiptId.Value = this.Id.Value;
            rl.LineNo.Value = nextNo;
            if (rl.Submit() != true) { Toaster.Error("消込明細の付け替えに失敗しました"); return; }
        }
        var trs = new ModuleSearcher<Receipt>();
        trs.AddEquals(e => e.Id.Value, tid);
        var found = trs.ExecuteFirstOrDefault();
        if (found != null)
        {
            if (((Receipt)found).Delete() != true)
            {
                Toaster.Error("まとめ元の入金予定の削除に失敗しました（消込明細は移動済みです。入金一覧を確認してください）");
                return;
            }
        }
    }
    Amount.Value = LinesTotal();
    if (this.Submit() != true) { Toaster.Error("入金額の更新に失敗しました"); return; }
    ReloadLines();
    Toaster.Success($"入金 {targets.Count + 1} 件をまとめました（合計 {Amount.Value:#,0} 円）");
}

// まとめを解除して、明細ごとの入金予定に戻す
void Unmerge_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("まとめの解除は経理のみ実行できます");
        return;
    }
    if (HasSettleJournal(this.Id.Value))
    {
        Toaster.Error("確定済みの入金は解除できません（先に「入金を取り消す」を実行してください）");
        return;
    }
    var lines = GetLines();
    if (lines.Count < 2) { Toaster.Info("この入金は 1 件の請求書だけを消し込みます（解除するものがありません）"); return; }

    var answer = MessageBox.Show(
        $"まとめを解除して、消込明細 {lines.Count} 件をそれぞれ別の入金予定に戻します。よろしいですか？",
        "解除する", "キャンセル");
    if (answer != "解除する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var first = true;
    var keepAmount = 0;
    foreach (var rl in lines)
    {
        if (first)
        {
            first = false;
            keepAmount = rl.Amount.Value ?? 0;
            rl.LineNo.Value = 1;
            // **null は「変更なし」で正常**（既に行番号 1 のことがある）。false だけを失敗として扱う
            if (rl.Submit() == false) { Toaster.Error("消込明細の更新に失敗しました"); return; }
            continue;
        }
        var nr = new Receipt();
        nr.ReceiptDate.Value = ReceiptDate.Value;
        nr.Amount.Value = rl.Amount.Value ?? 0;
        nr.Method.Value = Method.Value;
        nr.InvoiceRef.Value = rl.InvoiceRef.Value;   // 移行の名残。1 行の入金では従来経路が読む
        nr.Note.Value = "合算入金の解除で分割された入金予定";
        if (nr.Submit() != true) { Toaster.Error("入金予定の作成に失敗しました"); return; }
        var ns = new ModuleSearcher<Receipt>();
        ns.AddEquals(e => e.InvoiceRef.Value, rl.InvoiceRef.Value);
        ns.OrderByDescending(e => e.Id.Value);
        ns.Limit(1);
        var created = ns.ExecuteFirstOrDefault();
        if (created == null) { Toaster.Error("作成した入金予定を取得できませんでした"); return; }
        // 新しい入金予定にはトリガ（ddl/780）が明細を 1 行作っている。
        // こちらの行を移すと 2 行になるので、**トリガが作った行を消してから**移す
        var dupS = new ModuleSearcher<ReceiptLine>();
        dupS.AddEquals(l => l.ReceiptId.Value, ((Receipt)created).Id.Value);
        foreach (var drow in dupS.Execute())
        {
            var dl = (ReceiptLine)drow;
            if ($"{dl.Id.Value}" == $"{rl.Id.Value}") continue;
            if (dl.Delete() != true) { Toaster.Error("重複した消込明細の削除に失敗しました"); return; }
        }
        rl.ReceiptId.Value = ((Receipt)created).Id.Value;
        rl.LineNo.Value = 1;
        if (rl.Submit() == false) { Toaster.Error("消込明細の付け替えに失敗しました"); return; }
    }
    Amount.Value = keepAmount;
    if (this.Submit() == false) { Toaster.Error("入金額の更新に失敗しました"); return; }
    ReloadLines();
    Toaster.Success($"まとめを解除しました（{lines.Count} 件の入金予定に戻しました）");
}

// 合算入金の確定（明細が 2 行以上のときだけ通る）。
// 借方は現預金 1 行、貸方は**請求書ごとに 1 行**——銀行明細 1 行に対して帳簿も 1 伝票にする（ADR-0071）
void ConfirmMulti()
{
    var lines = GetLines();
    if (Method.Value == "offset")
    {
        Toaster.Error("相殺入金は合算できません（相殺は仕入先請求と 1 対 1 で対応します）");
        return;
    }
    if (ReceiptDate.Value == null) { Toaster.Error("入金日を入力してください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 過入金ガード（請求書ごと）。拒否された金額をレコードに残さないよう保存より前に見る
    foreach (var rl in lines)
    {
        var iv = FindInvoice(rl.InvoiceRef.Value);
        if (iv == null) { Toaster.Error("消込明細の請求書が見つかりません"); return; }
        int gross = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
        int others = SumReceipts(rl.InvoiceRef.Value, true);
        int remain = gross - others;
        var amt = rl.Amount.Value ?? 0;
        if (amt <= 0) { Toaster.Error($"{iv.InvoiceNo.Value} の充当額が 0 円以下です"); return; }
        if (amt > remain)
        {
            Toaster.Error($"{iv.InvoiceNo.Value} への充当額 {amt:#,0} 円が請求残額 {remain:#,0} 円を超えています。まとめを解除して金額を直してください");
            return;
        }
    }

    Amount.Value = LinesTotal();
    if (this.ValidateInput() != true) { Toaster.Error("入力内容を確認してください"); return; }
    if (this.Submit() == false) { Toaster.Error("入金の保存に失敗しました"); return; }

    if (HasSettleJournal(this.Id.Value)) { Toaster.Error("この入金の消込仕訳は既に生成済みです"); return; }

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
    if (((FiscalPeriod)period).Status.Value == "closed") { Toaster.Error("入金日の期間は締め済みです。仕訳は手動で起票してください"); return; }

    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1020", "1000", "1100");
    object bankId = null;
    object cashId = null;
    object arId = null;
    foreach (var a in accS.Execute())
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1020") { bankId = acc.Id.Value; }
        if (acc.Code.Value == "1000") { cashId = acc.Id.Value; }
        if (acc.Code.Value == "1100") { arId = acc.Id.Value; }
    }
    if (arId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }
    object debitId = (Method.Value == "cash") ? cashId : bankId;
    if (debitId == null) { Toaster.Error("入金先の科目（普通預金 1020 / 現金 1000）がありません"); return; }

    // 行の内容はプリミティブの並行リストに組む（CLB-039・ISSUE-0006）
    var dcList = new List<string>();
    var accList = new List<object>();
    var amtList = new List<int>();
    var projList = new List<object>();
    var descList = new List<string>();
    var invNos = "";
    dcList.Add("D"); accList.Add(debitId); amtList.Add(LinesTotal());
    projList.Add(null); descList.Add("入金（合算）");
    foreach (var rl in lines)
    {
        var iv = FindInvoice(rl.InvoiceRef.Value);
        dcList.Add("C"); accList.Add(arId); amtList.Add(rl.Amount.Value ?? 0);
        projList.Add(iv == null ? null : iv.ProjectRef.Value);
        descList.Add($"入金 {(iv == null ? "" : iv.InvoiceNo.Value)}");
        if (invNos != "") { invNos = invNos + ", "; }
        invNos = invNos + (iv == null ? "" : iv.InvoiceNo.Value);
    }

    var nextNo = new JournalEntry().NextJournalNo(typedFy.Id.Value);
    var je = new JournalEntry();
    je.EntryDate.Value = ReceiptDate.Value;
    je.EntryType.Value = "auto";
    je.Description.Value = $"入金（合算） {invNos}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "receipt";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(dcList.Count);
    var idx = 0;
    foreach (var lr in je.Lines.Rows)
    {
        var l = (JournalLine)lr;
        l.LineNo.Value = idx + 1;
        l.Dc.Value = dcList[idx];
        l.Account.Value = accList[idx];
        l.Amount.Value = amtList[idx];
        l.InputAmount.Value = amtList[idx];
        l.TaxInputMode.Value = "none";
        l.Description.Value = descList[idx];
        if (projList[idx] != null) { l.ProjectRef.Value = projList[idx]; }
        idx = idx + 1;
    }
    je.MarkAllLinesOutOfScope();
    je.FillMissingDepartments();
    if (je.Submit() != true) { Toaster.Error("消込仕訳の生成に失敗しました"); return; }

    // 請求書ごとに状態を更新する
    foreach (var rl in lines)
    {
        var iv = FindInvoice(rl.InvoiceRef.Value);
        if (iv == null) continue;
        int gross = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
        int received = SumReceipts(rl.InvoiceRef.Value, false);
        var st = "issued";
        if (received >= gross && gross > 0) { st = "paid"; }
        else if (received > 0) { st = "partial"; }
        iv.Status.Value = st;
        if (iv.Submit() != true) { Toaster.Warn($"{iv.InvoiceNo.Value} の状態更新に失敗しました（仕訳は生成済みです）"); }
        // 一部入金になった請求書には残額の入金予定を作る（BUG-0381）。
        // 1 対 1 の経路には必ずある処理で、これが無いと**残額が入金一覧から消えて回収漏れに気づけない**
        if (st == "partial")
        {
            var remainAfter = gross - received;
            if (remainAfter > 0) { CreateRemainderPendingReceipt(remainAfter, iv); }
        }
    }

    UpdateButtons();
    Toaster.Success($"仕訳 No.{nextNo} を生成しました（合算入金 {LinesTotal():#,0} 円 / 請求書 {lines.Count} 件）");
}

// 合算入金の取消。**入金予定の統合（1 請求書前提の後始末）はしない**——
// 合算はもともと「まとめた 1 本の入金」なので、取り消したら未確定の合算入金に戻すのが素直
void CancelMulti()
{
    var je = FindReceiptJournal();
    if (je == null) { Toaster.Error("この入金の消込仕訳が見つかりません"); return; }
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
    var answer = MessageBox.Show(
        $"消込仕訳 No.{jeNo} を削除して、合算入金を未確定に戻します（まとめた明細はそのまま残ります）。よろしいですか？",
        "取り消す", "キャンセル");
    if (answer != "取り消す") return;

    using var loading = LoadingService.StartLoading(0);
    if (!DeleteJournalEntryWithLines(je))
    {
        Toaster.Error("消込仕訳の削除に失敗しました（入金は確定済みのままです）");
        return;
    }
    foreach (var rl in GetLines())
    {
        var iv = FindInvoice(rl.InvoiceRef.Value);
        if (iv == null) continue;
        int gross = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
        int received = SumReceipts(rl.InvoiceRef.Value, false);
        var st = "issued";
        if (received >= gross && gross > 0) { st = "paid"; }
        else if (received > 0) { st = "partial"; }
        iv.Status.Value = st;
        if (iv.Submit() != true) { Toaster.Warn($"{iv.InvoiceNo.Value} の状態更新に失敗しました（仕訳は削除済みです）"); }
    }
    UpdateButtons();
    Toaster.Success($"仕訳 No.{jeNo} を削除し、合算入金を未確定に戻しました");
}
