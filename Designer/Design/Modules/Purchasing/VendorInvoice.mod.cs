// VendorInvoice.mod.cs — 仕入先請求書（買掛・支払管理 D-6）
// received(受領) →「未払計上」→ accrued →「支払登録」→ paid の一方向遷移。
// 未払計上: D 費用科目(本体) [+ D 仮払消費税1900(税)] / C 買掛金2000(税込)（EntryDate=請求日）
// 支払登録: D 買掛金2000(税込) / C 支払口座の帳簿科目（既定 普通預金1020。EntryDate=本日）
// 仕訳の型は ExpenseRequest.GenerateJournal / BankImport.PostAll と同じ規律。
//
// 案件（ProjectRef・ADR-0064）: 選択されていれば**両方の仕訳の全明細**へ引き継ぐ。
// これが無いと外注費・SES 仕入が案件別損益（ProjectProfit の直課費用）に一切乗らない。
// 引き継ぎ方は ExpenseRequest.GenerateJournal と同じ（レイアウト状態によって .Value が
// 未ロードのことがあるため、null なら DB から取り直す）。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "received";
        ReceivedDate.Value = DateOnly.FromDateTime(DateTime.Today);
    }
    UpdateButtons();
}

// この請求書を複製: 反復的な仕入（家賃・外注の月次請求・保守料など）を過去請求書から新規作成する。
// コピーする: 取引先・費用科目・部門・案件・税区分・税込金額・摘要・支払口座
// コピーしない: 請求書番号・請求日・支払期限（今回の請求書の実物から入力）・仕訳リンク・支払日。状態=受領
void Duplicate_OnClick()
{
    if (this.IsNewData) { Toaster.Error("保存済みの請求書のみ複製できます"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var copy = new VendorInvoice();
    copy.Partner.Value = Partner.Value;
    copy.ExpenseAccount.Value = ExpenseAccount.Value;
    copy.DepartmentRef.Value = DepartmentRef.Value;
    copy.ProjectRef.Value = ProjectRef.Value;
    copy.TaxCategoryRef.Value = TaxCategoryRef.Value;
    copy.Amount.Value = Amount.Value;
    copy.Description.Value = Description.Value;
    copy.BankAccountRef.Value = BankAccountRef.Value;
    copy.ReceivedDate.Value = DateOnly.FromDateTime(DateTime.Today);
    copy.Status.Value = "received";
    var ret = copy.Submit();
    if (ret != true) { Toaster.Error("複製に失敗しました"); return; }

    Toaster.Success("請求書を複製しました。請求書番号・請求日・支払期限・金額を実物に合わせて入力してください");

    // 作成した複製へ遷移（Submit 後の Id はテンポラリの可能性があるため DB から取り直す。
    // 直後の自動採番 PK＝最新行。VendorInvoice に Creator 列は無い）
    var s = new ModuleSearcher<VendorInvoice>();
    s.OrderByDescending(e => e.Id.Value);
    s.Limit(1);
    var created = s.ExecuteFirstOrDefault();
    if (created != null)
    {
        var typedCreated = (VendorInvoice)created;
        NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("VendorInvoice", $"{typedCreated.Id.Value}"));
    }
}

void UpdateButtons()
{
    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
    // 複製は保存済みの請求書に対する操作（未保存では出さない。驚き最小: 2026-08-03 UXレビュー）
    DuplicateButton.IsVisible = !this.IsNewData;
    AccrueButton.IsVisible = isAccounting && !this.IsNewData && Status.Value == "received";
    PayButton.IsVisible = isAccounting && !this.IsNewData && Status.Value == "accrued";
    // 逆遷移（ADR-0026）: 誤操作のリカバリ。仕訳の削除を伴うため経理のみ・締め済み期間はガード
    CancelAccrueButton.IsVisible = isAccounting && !this.IsNewData && Status.Value == "accrued";
    CancelPayButton.IsVisible = isAccounting && !this.IsNewData && Status.Value == "paid";
    // 削除は「受領（仕訳なし）」のみ。一覧の削除ボタンは撤去済み
    DeleteVendorInvoiceButton.IsVisible = !this.IsNewData && Status.Value == "received";
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

// source_type + source_id で自動仕訳を1件取得（無ければ null）
JournalEntry FindSourceJournal(string sourceType)
{
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.SourceType.Value, sourceType);
    s.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (JournalEntry)found;
}

// 案件（任意）の解決。レイアウト状態によっては .Value が未ロードのことがあるため、
// null のときは DB から取り直す（ExpenseRequest.GenerateJournal と同じ流儀）
object ResolveProjectId()
{
    var projectId = ProjectRef.Value;
    if (projectId != null) { return projectId; }
    if (this.IsNewData) { return null; }
    var s = new ModuleSearcher<VendorInvoice>();
    s.AddEquals(e => e.Id.Value, this.Id.Value);
    var self = s.ExecuteFirstOrDefault();
    if (self == null) { return null; }
    return ((VendorInvoice)self).ProjectRef.Value;
}

// 損益科目（費用・収益）かどうか。部門必須の判定に使う（ADR-0056）
bool IsProfitLossAccount(object accountId)
{
    if (accountId == null) { return false; }
    var s = new ModuleSearcher<Account>();
    s.AddEquals(e => e.Id.Value, accountId);
    var acc = s.ExecuteFirstOrDefault();
    if (acc == null) { return false; }
    var t = ((Account)acc).AccountType.Value;
    return t == "expense" || t == "revenue";
}

// 仕訳の日付が締め済み期間に落ちていないか（true=削除可能）
bool IsJournalPeriodOpen(JournalEntry je)
{
    if (je.EntryDate.Value == null) return true;
    var d = je.EntryDate.Value;
    var firstDay = new DateTime(d.Year, d.Month, 1);
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) return true;
    return ((FiscalPeriod)period).Status.Value != "closed";
}

// 未払計上の取消（accrued→received）: 未払計上仕訳を削除して受領に戻す
void CancelAccrue_OnClick()
{
    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
    if (!isAccounting) { Toaster.Error("未払計上の取消は経理のみ実行できます"); return; }
    if (Status.Value != "accrued") { Toaster.Error("未払計上済みの請求書のみ取り消せます"); return; }

    var je = FindSourceJournal("vendor_invoice");
    if (je != null)
    {
        if (!IsJournalPeriodOpen(je))
        {
            Toaster.Error("未払計上仕訳の期間が締め済みのため取り消せません（決算修正仕訳で対応してください）");
            return;
        }
        var result = MessageBox.Show($"未払計上を取り消しますか？（仕訳 No.{je.JournalNo.Value} を削除し、受領状態に戻します）", "取り消す", "キャンセル");
        if (result != "取り消す") return;
    }
    else
    {
        var result = MessageBox.Show("対応する仕訳が見つかりません。状態だけを受領に戻しますか？", "取り消す", "キャンセル");
        if (result != "取り消す") return;
    }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var deletedNo = 0;
    if (je != null)
    {
        if (je.JournalNo.Value != null) { deletedNo = (int)je.JournalNo.Value; }
        // FK 制約: vendor_invoices.accrual_entry_id が仕訳を参照しているため、先に参照を外してから削除する
        AccrualEntryId.Value = null;
        var retClear = this.Submit();
        if (retClear == false) { Toaster.Error("参照の解除に失敗しました（未払計上済のままです）"); return; }
        if (!DeleteJournalEntryWithLines(je))
        {
            AccrualEntryId.Value = je.Id.Value;
            this.Submit();
            Toaster.Error("仕訳の削除に失敗しました（未払計上済のままです）");
            return;
        }
    }
    AccrualEntryId.Value = null;
    Status.Value = "received";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("ステータスの更新に失敗しました"); return; }
    UpdateButtons();
    if (deletedNo > 0) { Toaster.Success($"未払計上を取り消しました（仕訳 No.{deletedNo} を削除）"); }
    else { Toaster.Success("未払計上を取り消しました"); }
}

// 支払の取消（paid→accrued）: 支払仕訳を削除して未払計上済みに戻す
void CancelPay_OnClick()
{
    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
    if (!isAccounting) { Toaster.Error("支払の取消は経理のみ実行できます"); return; }
    if (Status.Value != "paid") { Toaster.Error("支払済みの請求書のみ取り消せます"); return; }

    // 相殺で消し込まれた支払はここでは取り消せない（ADR-0035）。買掛側だけ accrued に戻すと
    // 入金側の消込仕訳が残ったまま再支払できてしまい二重払いになる——取消経路は入金側に一本化
    if (PaymentEntryId.Value != null)
    {
        var pjs = new ModuleSearcher<JournalEntry>();
        pjs.AddEquals(e => e.Id.Value, PaymentEntryId.Value);
        var pj = pjs.ExecuteFirstOrDefault();
        if (pj != null && ((JournalEntry)pj).SourceType.Value == "receipt")
        {
            Toaster.Error("この請求書は売掛金との相殺で消し込まれています。取消は 販売＞入金 の該当入金（入金を取り消す）から行ってください");
            return;
        }
    }

    var je = FindSourceJournal("vendor_payment");
    if (je != null)
    {
        if (!IsJournalPeriodOpen(je))
        {
            Toaster.Error("支払仕訳の期間が締め済みのため取り消せません（決算修正仕訳で対応してください）");
            return;
        }
        var result = MessageBox.Show($"支払を取り消しますか？（仕訳 No.{je.JournalNo.Value} を削除し、未払計上済みに戻します）", "取り消す", "キャンセル");
        if (result != "取り消す") return;
    }
    else
    {
        var result = MessageBox.Show("対応する仕訳が見つかりません。状態だけを未払計上済みに戻しますか？", "取り消す", "キャンセル");
        if (result != "取り消す") return;
    }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var deletedNo = 0;
    if (je != null)
    {
        if (je.JournalNo.Value != null) { deletedNo = (int)je.JournalNo.Value; }
        // FK 制約: vendor_invoices.payment_entry_id が仕訳を参照しているため、先に参照を外してから削除する
        PaymentEntryId.Value = null;
        var retClear = this.Submit();
        if (retClear == false) { Toaster.Error("参照の解除に失敗しました（支払済のままです）"); return; }
        if (!DeleteJournalEntryWithLines(je))
        {
            PaymentEntryId.Value = je.Id.Value;
            this.Submit();
            Toaster.Error("仕訳の削除に失敗しました（支払済のままです）");
            return;
        }
    }
    PaymentEntryId.Value = null;
    PaidDate.Value = null;
    Status.Value = "accrued";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("ステータスの更新に失敗しました"); return; }
    UpdateButtons();
    if (deletedNo > 0) { Toaster.Success($"支払を取り消しました（仕訳 No.{deletedNo} を削除）"); }
    else { Toaster.Success("支払を取り消しました"); }
}

// 受領状態の削除（仕訳が無いことを確認してから）
void DeleteVendorInvoice_OnClick()
{
    if (Status.Value != "received") { Toaster.Error("受領状態の請求書のみ削除できます（未払計上・支払を先に取り消してください）"); return; }
    if (FindSourceJournal("vendor_invoice") != null || FindSourceJournal("vendor_payment") != null)
    {
        Toaster.Error("この請求書に紐づく仕訳があるため削除できません");
        return;
    }
    var result = MessageBox.Show($"仕入先請求書「{InvoiceNo.Value}」を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;

    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("仕入先請求書を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("VendorInvoice"));
}

// ============ 未払計上（D 費用+税 / C 買掛金） ============

void Accrue_OnClick()
{
    if (Status.Value != "received") { Toaster.Error("受領状態の請求書のみ未払計上できます"); return; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("税込金額を入力してください"); return; }
    if (InvoiceDate.Value == null) { Toaster.Error("請求日を入力してください"); return; }
    if (ExpenseAccount.Value == null) { Toaster.Error("費用科目を選択してください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード
    var dup = new ModuleSearcher<JournalEntry>();
    dup.AddEquals(e => e.SourceType.Value, "vendor_invoice");
    dup.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (dup.Execute().Count > 0) { Toaster.Error("この請求書の未払計上仕訳は既に生成済みです"); return; }

    // 年度・期間の解決（請求日の月初日で解決＝境界日の罠回避）と締めガード
    var d = InvoiceDate.Value;
    var firstDay = new DateTime(d.Year, d.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("請求日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("請求日に対応する月次期間がありません"); return; }
    if (((FiscalPeriod)period).Status.Value == "closed") { Toaster.Error("請求日の期間は締め済みです。仕訳は手動で起票してください"); return; }

    // 損益科目の行には部門が要る（ADR-0056）。経理が画面を見て押す操作なので、
    // 全社共通で黙って埋めずにここで止めて選ばせる
    if (DepartmentRef.Value == null && IsProfitLossAccount(ExpenseAccount.Value))
    {
        Toaster.Error("部門を選択してください（損益科目の仕訳には部門が必要です）");
        return;
    }

    // 科目の解決（買掛金2000・仮払消費税1900）
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "2000", "1900");
    var accs = accS.Execute();
    object apAccountId = null;
    object purchaseTaxId = null;
    foreach (var am in accs)
    {
        var a = (Account)am;
        if (a.Code.Value == "2000") { apAccountId = a.Id.Value; }
        if (a.Code.Value == "1900") { purchaseTaxId = a.Id.Value; }
    }
    if (apAccountId == null) { Toaster.Error("買掛金(2000)の科目がありません"); return; }

    // 税額（税区分が課税仕入のとき内税計算）
    int gross = Amount.Value;
    var tax = 0;
    object taxCatId = TaxCategoryRef.Value;
    if (taxCatId != null)
    {
        var cs = new ModuleSearcher<TaxCategory>();
        cs.AddEquals(c => c.Id.Value, taxCatId);
        var found = cs.ExecuteFirstOrDefault();
        if (found != null)
        {
            var tcat = (TaxCategory)found;
            if (tcat.TaxationType.Value == "taxable_purchase" && tcat.Rate.Value != null)
            {
                var rs = new ModuleSearcher<TaxRate>();
                rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
                var foundRate = rs.ExecuteFirstOrDefault();
                if (foundRate != null)
                {
                    decimal pct = ((TaxRate)foundRate).RatePercent.Value ?? 0;
                    if (pct > 0) tax = gross * pct / (100 + pct);
                }
            }
        }
    }
    var baseAmount = gross - tax;

    // 採番
    var nextNo = NextJournalNo(typedFy.Id.Value);

    // 案件（任意）: 選ばれていれば仕訳の全行に引き継ぐ（案件別損益への直課・ADR-0064）
    var projectId = ResolveProjectId();

    // 仕訳生成
    var lineCount = (tax > 0) ? 3 : 2;
    var je = new JournalEntry();
    je.EntryDate.Value = d;
    je.EntryType.Value = "auto";
    je.Description.Value = $"仕入先請求 {InvoiceNo.Value} {Description.Value}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "vendor_invoice";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(lineCount);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = Description.Value;
        // 伝票ヘッダの部門を仕訳に伝搬する（販売伝票 ADR-0029 と同じ考え方・ADR-0056）
        if (DepartmentRef.Value != null) { l.Department.Value = DepartmentRef.Value; }
        if (projectId != null) { l.ProjectRef.Value = projectId; }
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = ExpenseAccount.Value;
            if (taxCatId != null) l.TaxCategory.Value = taxCatId;
            l.TaxInputMode.Value = (tax > 0) ? "inclusive" : "none";
            l.Amount.Value = baseAmount;
            l.InputAmount.Value = gross;
        }
        else if (idx == 2 && tax > 0)
        {
            l.Dc.Value = "D";
            l.Account.Value = purchaseTaxId;
            l.TaxCategory.Value = taxCatId;
            l.TaxInputMode.Value = "none";
            l.IsTaxLine.Value = true;
            l.ParentLineNo.Value = 1;
            l.Amount.Value = tax;
            l.InputAmount.Value = tax;
            l.Description.Value = "消費税（行1）";
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = apAccountId;
            l.TaxInputMode.Value = "none";
            l.Amount.Value = gross;
            l.InputAmount.Value = gross;
        }
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ok = je.Submit();
    if (ok != true) { Toaster.Error("未払計上仕訳の生成に失敗しました。ほかの人が同時に伝票を確定した可能性があります。もう一度お試しください"); return; }

    // 生成仕訳の id をリンク（DB から引き直し）
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "vendor_invoice");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var created = js.ExecuteFirstOrDefault();
    if (created != null) { AccrualEntryId.Value = ((JournalEntry)created).Id.Value; }

    Status.Value = "accrued";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("ステータスの更新に失敗しました（仕訳は生成済みです）"); return; }
    UpdateButtons();
    Toaster.Success($"未払計上 No.{nextNo} を生成しました（借方 {baseAmount:#,0} 円+税 / 貸方 買掛金 {gross:#,0} 円）");
}

// ============ 支払登録（D 買掛金 / C 預金） ============

void Pay_OnClick()
{
    if (Status.Value != "accrued") { Toaster.Error("未払計上済みの請求書のみ支払登録できます"); return; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("税込金額が不正です"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード
    var dup = new ModuleSearcher<JournalEntry>();
    dup.AddEquals(e => e.SourceType.Value, "vendor_payment");
    dup.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (dup.Execute().Count > 0) { Toaster.Error("この請求書の支払仕訳は既に生成済みです"); return; }

    // 年度・期間の解決（支払日=本日。月初日解決＋締めガード）
    var today = DateOnly.FromDateTime(DateTime.Today);
    var firstDay = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("本日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("本日に対応する月次期間がありません"); return; }
    if (((FiscalPeriod)period).Status.Value == "closed") { Toaster.Error("本日の期間は締め済みです"); return; }

    // 支払口座の帳簿科目（未選択なら普通預金1020）
    object ledgerId = null;
    if (BankAccountRef.Value != null)
    {
        var bs = new ModuleSearcher<BankAccount>();
        bs.AddEquals(b => b.Id.Value, BankAccountRef.Value);
        var bank = bs.ExecuteFirstOrDefault();
        if (bank != null) { ledgerId = ((BankAccount)bank).LedgerAccount.Value; }
    }
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "2000", "1020");
    var accs = accS.Execute();
    object apAccountId = null;
    object defaultBankId = null;
    foreach (var am in accs)
    {
        var a = (Account)am;
        if (a.Code.Value == "2000") { apAccountId = a.Id.Value; }
        if (a.Code.Value == "1020") { defaultBankId = a.Id.Value; }
    }
    if (ledgerId == null) { ledgerId = defaultBankId; }
    if (apAccountId == null || ledgerId == null) { Toaster.Error("買掛金(2000)または預金科目が見つかりません"); return; }

    int gross = Amount.Value;
    var nextNo = NextJournalNo(typedFy.Id.Value);

    // 案件（任意）: 支払仕訳にも引き継ぐ。買掛金・預金はどちらも損益科目ではないので
    // 案件別損益の数字は動かないが、案件で元帳を絞ったときに入出金まで追える（ADR-0064）
    var projectId = ResolveProjectId();

    var je = new JournalEntry();
    je.EntryDate.Value = today;
    je.EntryType.Value = "auto";
    je.Description.Value = $"支払 {InvoiceNo.Value} {Description.Value}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "vendor_payment";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(2);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = Description.Value;
        if (projectId != null) { l.ProjectRef.Value = projectId; }
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = apAccountId;
            l.TaxInputMode.Value = "none";
            l.Amount.Value = gross;
            l.InputAmount.Value = gross;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = ledgerId;
            l.TaxInputMode.Value = "none";
            l.Amount.Value = gross;
            l.InputAmount.Value = gross;
        }
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ok = je.Submit();
    if (ok != true) { Toaster.Error("支払仕訳の生成に失敗しました。ほかの人が同時に伝票を確定した可能性があります。もう一度お試しください"); return; }

    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "vendor_payment");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var created = js.ExecuteFirstOrDefault();
    if (created != null) { PaymentEntryId.Value = ((JournalEntry)created).Id.Value; }

    PaidDate.Value = today;
    Status.Value = "paid";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("ステータスの更新に失敗しました（仕訳は生成済みです）"); return; }
    UpdateButtons();
    Toaster.Success($"支払仕訳 No.{nextNo}（{gross:#,0} 円）を生成し支払済にしました");
}

// 伝票採番の正典は JournalEntry.NextJournalNo（BUG-0069 で一本化）。ここは呼ぶだけ
int NextJournalNo(object fiscalYearId)
{
    return new JournalEntry().NextJournalNo(fiscalYearId);
}
