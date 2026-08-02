// 費目×金額でテンプレートを選択 (ApprovalFlow の申請/再申請から呼ばれる)
// ADR-0023: approval_route_rules マスタ（システム管理 > 承認/承認ルート判定）を
// 優先度の小さい順に評価し、最初に一致した行のテンプレートを使う。
// 一致条件: 有効 かつ (費目指定なし or 申請の費目と一致) かつ 下限 ≦ 判定額 ≦ 上限（上限 NULL=無制限）
// テンプレートは ID で参照（テンプレート改名に影響されない）。名前を返す契約は従来どおり。
string SelectTemplateName()
{
    var amount = GetJudgeAmount();
    var catId = ExpenseCategoryRef.Value;

    var rs = new ModuleSearcher<ApprovalRouteRule>();
    rs.OrderBy(r => r.Priority.Value);
    var rules = rs.Execute();
    foreach (var rm in rules)
    {
        var r = (ApprovalRouteRule)rm;
        if (r.IsActive.Value != true) continue;
        if (r.ExpenseCategorySel.Value != null && !IsSameId(r.ExpenseCategorySel.Value, catId)) continue;
        var min = r.MinAmount.Value ?? 0;
        if (amount < min) continue;
        var max = r.MaxAmount.Value;
        if (max != null && amount > max) continue;
        return FindTemplateNameById(r.TemplateSel.Value);
    }
    Toaster.Error($"承認ルート判定に一致するルールがありません（判定額 {amount:#,0} 円）。システム管理 > 承認/承認ルート判定 を確認してください");
    return "";
}

// テンプレート ID → 名前（ApprovalFlow の名前ベース契約への橋渡し。見つからなければ ""）
string FindTemplateNameById(object templateId)
{
    if (templateId == null) return "";
    var s = new ModuleSearcher<ApprovalFlowTemplate>();
    s.AddEquals(t => t.Id.Value, templateId);
    var found = s.Execute();
    if (found.Count == 0)
    {
        Toaster.Error("承認ルート判定ルールが参照するテンプレートが見つかりません。システム管理 > 承認/承認ルート判定 を確認してください");
        return "";
    }
    var t = (ApprovalFlowTemplate)found[0];
    return t.Name.Value;
}

// ID の等値判定。SelectField 由来で値の型（string/decimal）が揃わないことがあるため文字列正規化で比較
bool IsSameId(object a, object b)
{
    return $"{a}" == $"{b}";
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
    // 領収書の未添付警告（U2-6: 申請はブロックしない。添付できない実務ケースを許容）
    if (Receipt.FileName == null || Receipt.FileName == "")
    {
        Toaster.Warn("領収書が添付されていません。後から添付するか、紙の原本を保管してください");
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

    // 申請後（フロー進行中/完了）は申請内容を変更不可。
    // 下書き（未申請の複製ドラフト／却下・キャンセルで差し戻し済み）は編集可。
    // 注: 未保存の子モジュールのフィールドを親から読むと「操作が存在しません」エラーになるため、
    //     フロー状態ではなく親自身の精算ステータスで判定する（却下/キャンセル時は
    //     OnApprovalFlowStatusChanged が draft に戻すので同値。2026-07-08）
    EditableGrid.IsEnabled = (SettlementStatus.Value == "draft");
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
        // 部門スナップショット: 申請時の申請者所属部門を記録（U2-8: 部門検索用。
        // 人事異動後も申請時点の部門で検索できる。再申請時は初回の値を保持）
        if (DepartmentRef.Value == null)
        {
            DepartmentRef.Value = CurrentUser.所属部門.Value;
        }
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
// 会計処理（仕訳生成・精算・完了）は経理専用（B-8）。
// 実費確定は申請者本人が行う業務のため全ユーザーに出す（ゲートしない）。
void UpdateAccountingButtons()
{
    var st = SettlementStatus.Value;
    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
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

    // 削除は「起案者本人 かつ 精算=下書き」のみ（2026-07-16 ユーザー決定。
    // 申請後・承認後の削除は意思決定履歴の抹消になるため不可。一覧の削除ボタンは全面撤去済み）
    DeleteDraftButton.IsVisible = !IsNewData && (st == "draft") && IsSameId(Creator.Value, CurrentUser.Id.Value);
}

// 下書きの削除（本人・下書きのみ。確認ダイアログ付き）
void DeleteDraft_OnClick()
{
    if (SettlementStatus.Value != "draft") { Toaster.Error("下書きの申請のみ削除できます"); return; }
    if (!IsSameId(Creator.Value, CurrentUser.Id.Value)) { Toaster.Error("自分が起案した申請のみ削除できます"); return; }
    var result = MessageBox.Show($"下書き「{Title.Value}」を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;

    using var loading = LoadingService.StartLoading(0);

    // 既知の限界: 承認フロー（子）の行はスクリプトから物理削除できず孤児として残る
    // （ModuleSearcher 検索行/ChildModule への Delete はいずれも CLB で無効を実測 2026-07-16。
    //   従来のリスト削除ボタンでも同じ挙動だった）。実害は「申請中ビューに出続けること」
    // だったため、MyApplication をキャンセル除外に変更して対処。孤児の物理掃除は sql CLI で可能。
    this.Delete();
    Toaster.Success("下書きを削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("ExpenseRequest"));
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

    // 会計年度の解決と締め済み期間ガード (境界日知見: 月末日は辞書順比較で失敗するため月初日で解決)
    // 利用日の期間が締め済み（または期間未設定）の場合は処理日（今日）に自動フォールバックして起票する
    // （実務の定石＝重要性の原則。締めた月の数字は動かさず、当月の費用として計上。摘要に元の利用日を明記）
    var entryDate = ExpenseDate.Value;
    var usedFallback = false;
    var typedFy = ResolveYearForDate(entryDate);
    var typedPeriod = ResolvePeriodForDate(entryDate);
    var origClosed = (typedPeriod != null && typedPeriod.Status.Value == "closed");
    if (typedFy == null || typedPeriod == null || origClosed)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fyToday = ResolveYearForDate(today);
        var periodToday = ResolvePeriodForDate(today);
        if (fyToday == null || periodToday == null || periodToday.Status.Value == "closed")
        {
            Toaster.Error("利用日の期間に起票できず、本日の期間も締め済みまたは未設定です。会計年度・月次期間の設定を確認してください");
            return;
        }
        // 年度跨ぎの警告（前期の費用を当期計上する場合。重要な金額は決算修正を検討）
        if (typedFy != null && fyToday.Id.Value != typedFy.Id.Value)
        {
            Toaster.Warn($"利用日（{ExpenseDate.Value:yyyy/MM/dd}）は前年度です。当期の費用として計上します。金額が重要な場合は決算修正をご検討ください");
        }
        entryDate = today;
        typedFy = fyToday;
        typedPeriod = periodToday;
        usedFallback = true;
    }

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

    // 申請者の所属部門を解決して全行に引き継ぐ (部門別予実に乗せる。B-7。未解決なら省略)
    // 注: レイアウトに出ていない Creator は遅延ロードで null のことがある (#60) →
    //     DB から自レコードを取り直して creator を確実に読む
    var creatorId = Creator.Value;
    if (creatorId == null)
    {
        var es = new ModuleSearcher<ExpenseRequest>();
        es.AddEquals(e => e.Id.Value, this.Id.Value);
        var self = es.ExecuteFirstOrDefault();
        if (self != null) { creatorId = ((ExpenseRequest)self).Creator.Value; }
    }
    object creatorDeptId = null;
    if (creatorId != null)
    {
        var us = new ModuleSearcher<AppUser>();
        us.AddEquals(u => u.Id.Value, creatorId);
        var creatorUser = us.ExecuteFirstOrDefault();
        if (creatorUser != null) { creatorDeptId = ((AppUser)creatorUser).所属部門.Value; }
    }

    // 案件（任意）: 申請に案件が選ばれていれば仕訳の全行に引き継ぐ（案件別損益への直課）
    // レイアウト状態によっては .Value が未ロードのことがあるため（#60）、null なら DB から取り直す
    var projectId = ProjectRef.Value;
    if (projectId == null)
    {
        var prjS = new ModuleSearcher<ExpenseRequest>();
        prjS.AddEquals(e => e.Id.Value, this.Id.Value);
        var selfPrj = prjS.ExecuteFirstOrDefault();
        if (selfPrj != null) { projectId = ((ExpenseRequest)selfPrj).ProjectRef.Value; }
    }

    // 仕訳生成 (docs/04 の税行方式: 本体行 + is_tax_line 行 + 貸方行)
    var lineCount = (tax > 0) ? 3 : 2;
    var je = new JournalEntry();
    je.EntryDate.Value = entryDate;
    je.EntryType.Value = "auto";
    if (usedFallback) { je.Description.Value = $"経費精算 {Title.Value}（利用日 {ExpenseDate.Value:yyyy/MM/dd}）"; }
    else { je.Description.Value = $"経費精算 {Title.Value}"; }
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
        if (creatorDeptId != null) { l.Department.Value = creatorDeptId; }
        if (projectId != null) { l.ProjectRef.Value = projectId; }
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

    // 固定資産計上対象なら台帳へ自動登録 (取得価額は税抜本体額。部門は仕訳と同じく申請者の所属部門)
    if (IsFixedAsset.Value == true)
    {
        RegisterFixedAsset(debitAccountId, baseAmount, creatorDeptId);
    }

    SettlementStatus.Value = "accounting";
    var ret2 = this.Submit();
    if (ret2 == false) { Toaster.Error("精算ステータスの更新に失敗しました"); return; }
    UpdateAccountingButtons();
    Toaster.Success($"仕訳 No.{nextNo} を生成しました（借方 {debitName} {baseAmount:#,0} 円 / 貸方 未払金 {gross:#,0} 円）");
    if (usedFallback)
    {
        Toaster.Info($"利用日（{ExpenseDate.Value:yyyy/MM/dd}）の期間が締め済みのため、本日（{entryDate:yyyy/MM/dd}）日付で起票しました（摘要に利用日を記載）");
    }
}

// この申請を複製: 反復的な経費（定期券・毎月の会費・恒例の会議費など）を過去申請から新規作成する。
// コピーする: 件名・金額・目的・申請区分・支払先区分・費目・案件・見込み額・取引先・接待情報・固定資産フラグ
// コピーしない: 利用日(=今日)・領収書添付・実費・承認履歴・精算ステータス(=下書き)。精算対象者は複製した本人
void Duplicate_OnClick()
{
    if (this.IsNewData) { Toaster.Error("保存済みの申請のみ複製できます"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var copy = new ExpenseRequest();
    copy.Title.Value = Title.Value;
    copy.Amount.Value = Amount.Value;
    copy.Purpose.Value = Purpose.Value;
    copy.RequestType.Value = RequestType.Value;
    copy.PayeeType.Value = PayeeType.Value;
    copy.ExpenseCategoryRef.Value = ExpenseCategoryRef.Value;
    copy.ProjectRef.Value = ProjectRef.Value;
    copy.EstimatedAmount.Value = EstimatedAmount.Value;
    copy.PayeePartner.Value = PayeePartner.Value;
    copy.EntertainmentGuest.Value = EntertainmentGuest.Value;
    copy.EntertainmentCount.Value = EntertainmentCount.Value;
    copy.EntertainmentPurpose.Value = EntertainmentPurpose.Value;
    copy.IsFixedAsset.Value = IsFixedAsset.Value;
    copy.ExpenseDate.Value = DateOnly.FromDateTime(DateTime.Today);
    copy.PayeeUser.Value = CurrentUser.Id.Value;
    copy.SettlementStatus.Value = "draft";
    var ret = copy.Submit();
    if (ret != true) { Toaster.Error("複製に失敗しました"); return; }

    // 作成した複製を DB から取り直す（Submit 後の Id はテンポラリの可能性があるため）
    var s = new ModuleSearcher<ExpenseRequest>();
    s.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    s.OrderByDescending(e => e.Id.Value);
    s.Limit(1);
    var created = s.ExecuteFirstOrDefault();
    if (created == null) { Toaster.Error("複製の取得に失敗しました"); return; }
    var typedCreated = (ExpenseRequest)created;

    // 承認フローの行を Draft で作成し、FK（approval_flow_id）を複製に張る。
    // 子行が無い親は CLB が子モジュールを実体化せず申請ボタンが出ない（2026-07-08 実測）。
    // 未保存インスタンスの ChildModule 参照は「操作が存在しません」になるため、
    // 実列バインドの ApprovalFlowIdRaw 経由でリンクする
    var flow = new ApprovalFlow();
    flow.Status.Value = "Draft";
    flow.AttemptNo.Value = 1;
    flow.ParentModuleName.Value = "ExpenseRequest";
    flow.ParentId.Value = $"{typedCreated.Id.Value}";
    var retFlow = flow.Submit();
    if (retFlow != true) { Toaster.Error("承認フローの初期化に失敗しました"); return; }

    var fs = new ModuleSearcher<ApprovalFlow>();
    fs.OrderByDescending(f => f.Id.Value);
    fs.Limit(1);
    var newFlow = fs.ExecuteFirstOrDefault();
    if (newFlow != null)
    {
        typedCreated.ApprovalFlowIdRaw.Value = ((ApprovalFlow)newFlow).Id.Value;
        typedCreated.Submit();
    }

    Toaster.Success("申請を複製しました。利用日・金額を確認して申請してください");
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("ExpenseRequest", $"{typedCreated.Id.Value}"));
}

// 月初日で年度を解決（境界日の罠回避）。該当なしは null
FiscalYear ResolveYearForDate(var d)
{
    var first = new DateOnly(d.Year, d.Month, 1);
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, first);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, first);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalYear)found;
}

// 月初日で月次期間を解決（境界日の罠回避）。該当なしは null
FiscalPeriod ResolvePeriodForDate(var d)
{
    var first = new DateOnly(d.Year, d.Month, 1);
    var s = new ModuleSearcher<FiscalPeriod>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, first);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, first);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalPeriod)found;
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
void RegisterFixedAsset(object assetAccountId, int baseAmount, object departmentId)
{
    var code = AssetNo.Value;
    if (code == null || code == "") { code = $"EXP-{this.Id.Value}"; }
    var fs = new ModuleSearcher<FixedAsset>();
    fs.AddEquals(f => f.Code.Value, code);
    if (fs.Execute().Count > 0) return;

    var fa = new FixedAsset();
    fa.Code.Value = code;
    fa.Name.Value = Title.Value;
    if (departmentId != null) { fa.Department.Value = departmentId; }
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
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("精算（支払仕訳の生成）は経理のみ実行できます");
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

// AI 読み取り（AiReceiptReader）完了時: プログラム的なフィールド更新では OnDataChanged が
// 発火しないため、出し分け・固定資産判定を明示的に再評価する。
// AI はオプトイン（このコントロールを使ったときだけ実行。通常の領収書アップロードは手入力のまま）
void AiImport_Completed()
{
    UpdateFixedAssetSuggestion();
    UpdateVisibility();
    Toaster.Info("AI読み取り結果を反映しました。内容を確認・修正のうえ申請してください");
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
