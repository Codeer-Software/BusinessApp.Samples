// 費目×金額でテンプレートを選択 (ApprovalFlow の申請/再申請から呼ばれる)
// ルート: 判定額 < EXP_APPROVAL_MID(3万) = 課長 / MID〜HIGH(20万) = 部長 / HIGH以上 = 部長＋総務
//         交際費は金額によらず総務を併載。閾値は system_thresholds 参照（ハードコード禁止）
string SelectTemplateName()
{
    var amount = GetJudgeAmount();
    var mid = GetThresholdAmount("EXP_APPROVAL_MID");
    var high = GetThresholdAmount("EXP_APPROVAL_HIGH");
    var cat = FindSelectedCategory();
    var isEnt = (cat != null) && (cat.IsEntertainment.Value == true);
    var needsGA = isEnt || (high > 0 && amount >= high);

    if (mid > 0 && amount >= mid)
    {
        return needsGA ? "経費_部長＋総務" : "経費_部長のみ";
    }
    return needsGA ? "経費_課長＋総務" : "経費_課長のみ";
}

// 承認ルートの判定額: 立替精算は実費。事前申請は見込み額、実費確定後は実費
int GetJudgeAmount()
{
    if (RequestType.Value == "advance")
    {
        if (Amount.Value != null && Amount.Value > 0) return Amount.Value;
        return EstimatedAmount.Value ?? 0;
    }
    return Amount.Value ?? 0;
}

// 申請前の業務チェック (ApprovalFlow の申請/再申請から呼ばれる契約メソッド)
bool ValidateForApply()
{
    if (this.ValidateInput() != true)
    {
        Toaster.Error("入力内容を確認してください");
        return false;
    }
    if (RequestType.Value == "advance" && GetJudgeAmount() <= 0)
    {
        Toaster.Error("事前申請では見込み額を入力してください");
        return false;
    }
    if (RequestType.Value == "reimburse" && GetJudgeAmount() <= 0)
    {
        Toaster.Error("金額を入力してください");
        return false;
    }
    if (PayeeType.Value == "partner" && PayeePartner.Value == null)
    {
        Toaster.Error("支払取引先を選択してください");
        return false;
    }
    if (PayeeType.Value != "partner" && PayeeUser.Value == null)
    {
        Toaster.Error("精算対象者を選択してください");
        return false;
    }
    var cat = FindSelectedCategory();
    if (cat == null)
    {
        Toaster.Error("費目を選択してください");
        return false;
    }
    if (cat.IsEntertainment.Value == true)
    {
        var guestOk = !string.IsNullOrEmpty(EntertainmentGuest.Value);
        var countOk = (EntertainmentCount.Value ?? 0) > 0;
        var purposeOk = !string.IsNullOrEmpty(EntertainmentPurpose.Value);
        if (!guestOk || !countOk || !purposeOk)
        {
            Toaster.Error("交際費は相手先・参加人数・目的の入力が必須です");
            return false;
        }
    }
    return true;
}

void OnAfterInitialization()
{
    if (IsNewData)
    {
        // 新規時: ApprovalFlow を初期化。this.Id.Value は @temporary:guid だが、
        // CLB の TemporaryIdResolver が双方向サイクルを自動解決する。
        ApprovalFlow.ChildModule.Initialize("ExpenseRequest", this.Id.Value, SelectTemplateName());

        // 既定値: 立替精算 / 社員へ精算（対象者=本人） / 精算ステータス=下書き
        RequestType.Value = "reimburse";
        PayeeType.Value = "employee";
        PayeeUser.Value = CurrentUser.Id.Value;
        SettlementStatus.Value = "draft";
        UpdateVisibility();
        UpdateAccountingButtons();
        return;
    }

    // 申請後 (新規でない) は申請内容を変更不可。却下/キャンセル時のみ再申請のため編集可。
    var flowStatus = ApprovalFlow.ChildModule.Status.Value;
    var reopenable = (flowStatus == "Rejected" || flowStatus == "Cancelled");
    EditableGrid.IsEnabled = reopenable;
    UpdateVisibility();
    UpdateAccountingButtons();
}

// ============================================================
// 精算ステータスと経理処理 (B2-4)
// draft → applying(申請) → approved(承認完了) → accounting(仕訳生成)
//       → settled(精算=支払済) → completed(完了)。前半はフロー連動、後半は経理操作。
// ============================================================

// ApprovalFlow からの状態変化通知 (契約メソッド。親 Submit の直前に呼ばれる)
void OnApprovalFlowStatusChanged(string flowStatus)
{
    if (flowStatus == "Pending")
    {
        SettlementStatus.Value = "applying";
    }
    else if (flowStatus == "Approved")
    {
        // 経理処理以降へ進んでいる場合は巻き戻さない
        var st = SettlementStatus.Value;
        if (st == null || st == "" || st == "draft" || st == "applying") SettlementStatus.Value = "approved";
    }
    else if (flowStatus == "Rejected" || flowStatus == "Cancelled")
    {
        SettlementStatus.Value = "draft";
    }
    UpdateAccountingButtons();
}

// 経理ボタンと精算ステータス表示の出し分け
// 会計処理（仕訳生成・精算・完了）は経理ロール専用（B-8）。
// 実費確定は申請者本人が行う業務のため全ユーザーに出す（ゲートしない）。
void UpdateAccountingButtons()
{
    var st = SettlementStatus.Value;
    var isAccounting = (CurrentUser.Role.Value == "accounting");
    SettlementStatusLabel.IsVisible = !IsNewData;
    SettlementStatus.IsVisible = !IsNewData;

    // 事前申請は承認後に実費を確定してから仕訳生成に進む
    var isAdvance = (RequestType.Value == "advance");
    var actualConfirmed = (Amount.Value != null && Amount.Value > 0);
    var needsActual = !IsNewData && (st == "approved") && isAdvance && !actualConfirmed;
    ActualAmountLabel.IsVisible = needsActual;
    ActualAmountInput.IsVisible = needsActual;
    ConfirmActualButton.IsVisible = needsActual;

    GenerateJournalButton.IsVisible = isAccounting && !IsNewData && (st == "approved") && !needsActual;
    SettleButton.IsVisible = isAccounting && !IsNewData && (st == "accounting");
    CompleteButton.IsVisible = isAccounting && !IsNewData && (st == "settled");
}

// 事前申請の実費確定: 見込みとの乖離が大きければ再承認、問題なければそのまま経理処理へ
// 超過判定: (a) 承認ルートの区分（3万/20万）を跨ぐ (b) 実費 > 見込み × EXP_OVERRUN_RATE(%)
void ConfirmActual_OnClick()
{
    if (SettlementStatus.Value != "approved" || RequestType.Value != "advance") return;
    var actual = ActualAmountInput.Value ?? 0;
    if (actual <= 0) { Toaster.Error("実費（税込）を入力してください"); return; }
    var estimated = EstimatedAmount.Value ?? 0;

    var routeBefore = SelectTemplateName();
    Amount.Value = actual;
    var routeAfter = SelectTemplateName();

    var overRate = GetThresholdAmount("EXP_OVERRUN_RATE");
    var crossed = (routeBefore != routeAfter);
    var overLimit = (overRate > 0) && (actual * 100 > estimated * overRate);

    if (crossed || overLimit)
    {
        // 再承認: フローを Pending に戻し実費でルート再解決（精算ステータスは通知で applying に戻る）
        ApprovalFlow.ChildModule.ReapproveForOverrun($"実費 {actual:#,0} 円が見込み {estimated:#,0} 円を超過したため再承認");
    }
    else
    {
        var ret = this.Submit();
        if (ret != true) { Toaster.Error("実費の保存に失敗しました"); return; }
        Toaster.Success($"実費 {actual:#,0} 円を確定しました。仕訳を生成できます");
    }
    UpdateAccountingButtons();
}

// 経理: 仕訳を生成 (approved → accounting)
// D: 費目の既定勘定科目（固定資産計上時は工具器具備品1520）+ 仮払消費税行 / C: 未払金2020
void GenerateJournal_OnClick()
{
    if (SettlementStatus.Value != "approved") { Toaster.Error("承認済の申請のみ仕訳を生成できます"); return; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("金額が入力されていません"); return; }
    if (ExpenseDate.Value == null) { Toaster.Error("利用日が入力されていません"); return; }
    var cat = FindSelectedCategory();
    if (cat == null) { Toaster.Error("費目が選択されていません"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "expense");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) { Toaster.Error("この申請の仕訳は既に生成済みです"); return; }

    // 会計年度の解決と締め済み期間ガード (JournalEntry.SaveEntry と同じ規律)
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, ExpenseDate.Value);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, ExpenseDate.Value);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("利用日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, ExpenseDate.Value);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, ExpenseDate.Value);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("利用日に対応する月次期間がありません"); return; }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("利用日の期間は締め済みです。仕訳は手動で起票してください"); return; }

    // 借方科目: 通常=費目の既定科目 / 固定資産計上=工具器具備品(1520)
    var debitAccountId = cat.DefaultAccount.Value;
    var debitName = cat.Name.Value;
    if (IsFixedAsset.Value == true)
    {
        var accS = new ModuleSearcher<Account>();
        accS.AddEquals(e => e.Code.Value, "1520");
        var assetAcc = accS.ExecuteFirstOrDefault();
        if (assetAcc == null) { Toaster.Error("工具器具備品(1520)の科目がありません"); return; }
        debitAccountId = ((Account)assetAcc).Id.Value;
        debitName = "工具器具備品";
    }
    if (debitAccountId == null) { Toaster.Error("費目に既定勘定科目が設定されていません"); return; }

    // 貸方科目: 未払金(2020) / 税行科目: 仮払消費税(1900)
    var apS = new ModuleSearcher<Account>();
    apS.AddIn(e => e.Code.Value, "2020", "1900");
    var settleAccounts = apS.Execute();
    object apAccountId = null;
    object purchaseTaxAccountId = null;
    foreach (var a in settleAccounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "2020") { apAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1900") { purchaseTaxAccountId = acc.Id.Value; }
    }
    if (apAccountId == null) { Toaster.Error("未払金(2020)の科目がありません"); return; }

    // 税額: レシート記載 (TaxAmount) を優先、なければ税区分の税率で内税計算 (切り捨て)
    int gross = Amount.Value;
    int tax = CalcExpenseTax(cat, gross);
    int baseAmount = gross - tax;

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

    // 仕訳生成 (docs/04 の税行方式: 本体行 + is_tax_line 行 + 貸方行)
    var lineCount = (tax > 0) ? 3 : 2;
    var je = new JournalEntry();
    je.EntryDate.Value = ExpenseDate.Value;
    je.EntryType.Value = "auto";
    je.Description.Value = $"経費精算 {Title.Value}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "expense";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(lineCount);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = Title.Value;
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = debitAccountId;
            l.TaxCategory.Value = cat.DefaultTaxCategory.Value;
            l.TaxInputMode.Value = "inclusive";
            l.Amount.Value = baseAmount;
            l.InputAmount.Value = gross;
        }
        else if (idx == 2 && tax > 0)
        {
            l.Dc.Value = "D";
            l.Account.Value = purchaseTaxAccountId;
            l.TaxCategory.Value = cat.DefaultTaxCategory.Value;
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
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("仕訳の生成に失敗しました"); return; }

    // 固定資産計上対象なら台帳へ自動登録 (取得価額は税抜本体額)
    if (IsFixedAsset.Value == true)
    {
        RegisterFixedAsset(debitAccountId, baseAmount);
    }

    SettlementStatus.Value = "accounting";
    var ret2 = this.Submit();
    if (ret2 == false) { Toaster.Error("精算ステータスの更新に失敗しました"); return; }
    UpdateAccountingButtons();
    Toaster.Success($"仕訳 No.{nextNo} を生成しました（借方 {debitName} {baseAmount:#,0} 円 / 貸方 未払金 {gross:#,0} 円）");
}

// 税額の決定: 費目の既定税区分が課税仕入のときのみ。レシート記載の消費税額を優先
int CalcExpenseTax(ExpenseCategory cat, int gross)
{
    if (cat.DefaultTaxCategory.Value == null) return 0;
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Id.Value, cat.DefaultTaxCategory.Value);
    var found = cs.ExecuteFirstOrDefault();
    if (found == null) return 0;
    var tcat = (TaxCategory)found;
    if (tcat.TaxationType.Value != "taxable_purchase") return 0;
    if (TaxAmount.Value != null && TaxAmount.Value > 0) return TaxAmount.Value;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    decimal pct = ((TaxRate)foundRate).RatePercent.Value ?? 0;
    if (pct == 0) return 0;
    int tax = gross * pct / (100 + pct);
    return tax;
}

// 固定資産台帳への自動登録 (償却方法は仮=定額法。耐用年数と方法は経理が台帳で確定する)
void RegisterFixedAsset(object assetAccountId, int baseAmount)
{
    var code = AssetNo.Value;
    if (code == null || code == "") { code = $"EXP-{this.Id.Value}"; }
    var fs = new ModuleSearcher<FixedAsset>();
    fs.AddEquals(f => f.Code.Value, code);
    if (fs.Execute().Count > 0) return;

    var fa = new FixedAsset();
    fa.Code.Value = code;
    fa.Name.Value = Title.Value;
    fa.AssetAccount.Value = assetAccountId;
    fa.AcquisitionDate.Value = ExpenseDate.Value;
    fa.AcquisitionCost.Value = baseAmount;
    fa.DepreciationMethod.Value = "straight_line";
    fa.Status.Value = "in_use";
    fa.Memo.Value = $"経費申請「{Title.Value}」から自動登録。耐用年数・償却方法を確認してください";
    var ret = fa.Submit();
    if (ret == true) Toaster.Info($"固定資産台帳に {code} を登録しました（耐用年数・償却方法は台帳で確定してください）");
    else Toaster.Error("固定資産台帳への自動登録に失敗しました。手動で登録してください");
}

// 経理: 精算済にする (accounting → settled)
// B-6: 支払仕訳 (D 未払金2020 / C 普通預金1020) を生成してからステータスを進める
void Settle_OnClick()
{
    if (CurrentUser.Role.Value != "accounting")
    {
        Toaster.Error("精算（支払仕訳の生成）は経理ロールのみ実行できます");
        return;
    }
    if (SettlementStatus.Value != "accounting") return;
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("金額が入力されていません"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "expense_payment");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) { Toaster.Error("この申請の支払仕訳は既に生成済みです"); return; }

    // 支払日=今日。会計年度・期間の解決 (境界日知見: 期間解決はその月の月初日で行う)
    var payDate = DateOnly.FromDateTime(DateTime.Today);
    var monthFirst = new DateOnly(payDate.Year, payDate.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("支払日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("支払日に対応する月次期間がありません"); return; }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("支払日の期間は締め済みです"); return; }

    // 科目解決: 未払金2020 / 普通預金1020
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "2020", "1020");
    var accounts = accS.Execute();
    object apAccountId = null;
    object bankAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "2020") { apAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1020") { bankAccountId = acc.Id.Value; }
    }
    if (apAccountId == null) { Toaster.Error("未払金(2020)の科目がありません"); return; }
    if (bankAccountId == null) { Toaster.Error("普通預金(1020)の科目がありません"); return; }

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

    // 支払仕訳: D 未払金 / C 普通預金
    var je = new JournalEntry();
    je.EntryDate.Value = payDate;
    je.EntryType.Value = "auto";
    je.Description.Value = $"経費支払 {Title.Value}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "expense_payment";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(2);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = $"経費支払 {Title.Value}";
        l.TaxInputMode.Value = "none";
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = apAccountId;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = bankAccountId;
        }
    }
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("支払仕訳の生成に失敗しました"); return; }

    SettlementStatus.Value = "settled";
    var ret2 = this.Submit();
    if (ret2 != true) { Toaster.Error("精算ステータスの更新に失敗しました（支払仕訳は生成済みです）"); return; }
    UpdateAccountingButtons();
    Toaster.Success($"支払仕訳 No.{nextNo}（{amount:#,0} 円）を生成し精算済にしました");
}

// 経理: 完了にする (settled → completed)
void Complete_OnClick()
{
    if (SettlementStatus.Value != "settled") return;
    SettlementStatus.Value = "completed";
    var ret = this.Submit();
    if (ret != true) { Toaster.Error("更新に失敗しました"); SettlementStatus.Value = "settled"; return; }
    UpdateAccountingButtons();
    Toaster.Success("完了にしました");
}

void RequestType_OnDataChanged()
{
    UpdateVisibility();
}

void PayeeType_OnDataChanged()
{
    UpdateVisibility();
}

void ExpenseCategory_OnDataChanged()
{
    UpdateFixedAssetSuggestion();
}

void Amount_OnDataChanged()
{
    UpdateFixedAssetSuggestion();
}

void IsFixedAsset_OnDataChanged()
{
    UpdateVisibility();
}

// 選択中の費目マスタを取得（未選択なら null）
ExpenseCategory FindSelectedCategory()
{
    if (ExpenseCategoryRef.Value == null) return null;
    var s = new ModuleSearcher<ExpenseCategory>();
    s.AddEquals(c => c.Id.Value, ExpenseCategoryRef.Value);
    var found = s.Execute();
    if (found.Count == 0) return null;
    return (ExpenseCategory)found[0];
}

// 申請区分・支払先区分・費目に応じた項目の出し分け
void UpdateVisibility()
{
    // 見込み額: 事前申請のみ
    var isAdvance = (RequestType.Value == "advance");
    EstimatedAmountLabel.IsVisible = isAdvance;
    EstimatedAmount.IsVisible = isAdvance;

    // 支払先: 社員へ精算 ⇔ 取引先へ支払
    var toPartner = (PayeeType.Value == "partner");
    PayeeUserLabel.IsVisible = !toPartner;
    PayeeUser.IsVisible = !toPartner;
    PayeePartnerLabel.IsVisible = toPartner;
    PayeePartner.IsVisible = toPartner;

    var cat = FindSelectedCategory();

    // 交際費: 相手先・人数・目的が必須項目として出現
    var isEnt = (cat != null) && (cat.IsEntertainment.Value == true);
    EntGuestLabel.IsVisible = isEnt;
    EntertainmentGuest.IsVisible = isEnt;
    EntCountLabel.IsVisible = isEnt;
    EntertainmentCount.IsVisible = isEnt;
    EntPurposeLabel.IsVisible = isEnt;
    EntertainmentPurpose.IsVisible = isEnt;
    // 注: IsRequired はスクリプトから設定不可 (CommonMistakes #5)。
    // 交際費の必須チェックは申請時の検証 (B2-3 の SelectTemplateName 拡張と同時) で行う。

    // 固定資産: 資産性の費目でのみチェックボックスを出す。ON のとき資産管理番号を出す
    var isAssetCandidate = (cat != null) && (cat.IsAssetCandidate.Value == true);
    IsFixedAsset.IsVisible = isAssetCandidate;
    var showAssetNo = isAssetCandidate && (IsFixedAsset.Value == true);
    AssetNoLabel.IsVisible = showAssetNo;
    AssetNo.IsVisible = showAssetNo;
}

// 資産性費目 × 金額が少額基準 (system_thresholds: SMALL_ASSET_EXPENSE) 以上なら
// 固定資産計上対象を自動 ON にする（ユーザーは手動で外せる）
void UpdateFixedAssetSuggestion()
{
    var cat = FindSelectedCategory();
    var isAssetCandidate = (cat != null) && (cat.IsAssetCandidate.Value == true);

    if (!isAssetCandidate)
    {
        if (IsFixedAsset.Value == true) IsFixedAsset.Value = false;
        UpdateVisibility();
        return;
    }

    var amount = Amount.Value ?? 0;
    var limit = GetSmallAssetLimit();
    if (limit > 0 && amount >= limit && IsFixedAsset.Value != true)
    {
        IsFixedAsset.Value = true;
        Toaster.Info($"金額 {amount:#,0} 円 ≧ 少額基準 {limit:#,0} 円のため固定資産計上対象にしました（承認後に固定資産台帳へ登録されます）");
    }
    UpdateVisibility();
}

// 利用日（未入力なら常に有効な行）時点の SMALL_ASSET_EXPENSE 閾値を解決
int GetSmallAssetLimit()
{
    return GetThresholdAmount("SMALL_ASSET_EXPENSE");
}

// system_thresholds から指定コードの閾値を期間解決して取得（該当なしは 0）
int GetThresholdAmount(string code)
{
    var s = new ModuleSearcher<SystemThreshold>();
    var thresholds = s.Execute();
    var d = ExpenseDate.Value;
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
