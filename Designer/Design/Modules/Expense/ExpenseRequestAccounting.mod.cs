// ExpenseRequestAccounting.mod.cs — 経費申請の経理処理（ADR-0069 段階4）
//
// expense_request を指す 2 本目の「対象者別モジュール」。人ゲート＝HasAccountingAccess、行フィルタ無し。
// 経理がやること（仕訳生成 → 精算 → 完了）だけを持つ薄いモジュールで、申請の作成・編集・申請・承認は
// 一切持たない（それらは申請者用 ExpenseRequest／承認者用モジュールの仕事）。
//
// 明細は ExpenseRequestLineAccounting（同じ expense_request_lines を指す経理用モジュール）から読む。
// 申請者用の ExpenseRequestLine には行条件（Creator == CurrentUser）が入っているので、
// 経理がそちらを ModuleSearcher で引くと **エラーにならず 0 件になる**（_specs/ModuleDesign.md:141-149）。
// ここで型を間違えると「明細がありません」と言われるか、静かに空の仕訳ができる。

// ============================================================
// 明細の取得
// ============================================================

// この申請の明細（行番号を持つ確定済みの行だけ。入力欄の行は親を持たないので出てこない）
List<ExpenseRequestLineAccounting> GetLines()
{
    var result = new List<ExpenseRequestLineAccounting>();
    if (this.IsNewData) return result;
    var s = new ModuleSearcher<ExpenseRequestLineAccounting>();
    s.AddEquals(l => l.ExpenseRequestId.Value, this.Id.Value);
    s.OrderBy(l => l.LineNo.Value);
    foreach (var m in s.Execute())
    {
        var l = (ExpenseRequestLineAccounting)m;
        if (l.LineNo.Value == null) continue;
        result.Add(l);
    }
    return result;
}

// ============================================================
// 画面の出し分け
// ============================================================

void OnAfterInitialization()
{
    UpdateVisibility();
    UpdateAccountingButtons();
}

// 支払先区分に応じて「精算対象者」か「支払取引先」の片方だけ出す
void UpdateVisibility()
{
    var toPartner = (PayeeType.Value == "partner");
    PayeeUserLabel.IsVisible = !toPartner;
    PayeeUser.IsVisible = !toPartner;
    PayeePartnerLabel.IsVisible = toPartner;
    PayeePartner.IsVisible = toPartner;
}

// 精算ステータスに応じて経理ボタンを 1 つだけ出す
// draft/applying は経理の出番ではない。approved → 仕訳生成 → accounting → 精算 → settled → 完了
void UpdateAccountingButtons()
{
    var st = SettlementStatus.Value;

    // 事前申請は申請者が実費を確定してからでないと仕訳を生成できない
    var isAdvance = (RequestType.Value == "advance");
    var waitingActual = (st == "approved") && isAdvance && (ActualConfirmed.Value != true);

    GenerateJournalButton.IsVisible = (st == "approved") && !waitingActual;
    SettleButton.IsVisible = (st == "accounting");
    CompleteButton.IsVisible = (st == "settled");
    WaitingActualLabel.IsVisible = waitingActual;
}

// ============================================================
// 経理: 仕訳を生成 (approved → accounting)
// D: 明細ごとに費目の既定勘定科目（固定資産計上の行は工具器具備品1520）＋その行の仮払消費税行
// C: 未払金2020 を 1 行（合計）
// ============================================================
void GenerateJournal_OnClick()
{
    if (SettlementStatus.Value != "approved") { Toaster.Error("承認済の申請のみ仕訳を生成できます"); return; }
    if (ExpenseDate.Value == null) { Toaster.Error("計上日が入力されていません"); return; }

    var lines = GetLines();
    if (lines.Count == 0) { Toaster.Error("明細がありません"); return; }

    // 確定仕訳を作る操作で、この画面に取り消す導線が無い＝不可逆なので確認する（ADR-0062）
    var answer = MessageBox.Show(
        $"この申請の明細 {lines.Count} 行から未払計上の仕訳を生成します。生成した仕訳を取り消す導線はこの画面にありません"
        + "（誤りは振替伝票側で訂正してください）。よろしいですか？",
        "生成する", "キャンセル");
    if (answer != "生成する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード。ただし**仕訳はあるのに状態が approved のまま**なら、それは
    // 「仕訳の保存は成功したが直後の状態更新が失敗した」中断状態である（BUG-0311）。
    // 以前はここで一律にエラーを返していたため、**SQL で直すまで画面から先へ進めない詰み**になっていた。
    // 仕訳を作り直すのではなく、**状態だけ進めて自己修復する**——押し直せば直る形にしておく
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "expense");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var existing = js.Execute();
    if (existing.Count > 0)
    {
        // **固定資産の台帳登録も拾い直す。** 中断は仕訳の保存後・資産登録ループの途中でも起こりうるので、
        // 状態だけ進めると「資産が永久に台帳へ登録されない」まま先へ行ってしまう（敵対的レビュー指摘）。
        // RegisterFixedAsset は同じ管理番号が既にあれば登録しないので、押し直しても二重にはならない
        var reRegistered = 0;
        var reAssetNo = 0;
        var reLines = GetLines();
        object reAssetAccountId = null;
        var reAccS = new ModuleSearcher<Account>();
        reAccS.AddEquals(e => e.Code.Value, "1520");
        var reAssetAcc = reAccS.ExecuteFirstOrDefault();
        if (reAssetAcc != null) { reAssetAccountId = ((Account)reAssetAcc).Id.Value; }
        object reDeptId = null;
        var reCreatorId = Creator.Value;
        if (reCreatorId != null)
        {
            var reUs = new ModuleSearcher<AppUser>();
            reUs.AddEquals(u => u.Id.Value, reCreatorId);
            var reUser = reUs.ExecuteFirstOrDefault();
            if (reUser != null) { reDeptId = ((AppUser)reUser).所属部.Value; }
        }
        foreach (var l in reLines)
        {
            reAssetNo = reAssetNo + 1;
            if (l.IsFixedAsset.Value != true) continue;
            if (reAssetAccountId == null) continue;
            int reGross = l.Amount.Value ?? 0;
            int reBase = reGross - CalcLineTax(l);
            var reDate = ((JournalEntry)existing[0]).EntryDate.Value;
            if (RegisterFixedAsset(l, reAssetNo, reAssetAccountId, reBase, reDeptId, reDate)) reRegistered = reRegistered + 1;
        }

        SettlementStatus.Value = "accounting";
        var retFix = this.Submit();
        if (retFix != true)
        {
            Toaster.Error("この申請の仕訳は既に生成済みですが、精算ステータスの更新に失敗しました。画面を開き直してもう一度お試しください");
            return;
        }
        UpdateAccountingButtons();
        var fixedNo = ((JournalEntry)existing[0]).JournalNo.Value;
        var addText = (reRegistered > 0) ? $"（あわせて固定資産 {reRegistered} 件を台帳へ登録しました）" : "";
        Toaster.Info($"仕訳 No.{fixedNo} は既に生成されていました。二重には作らず、精算ステータスだけ「経理処理中」に進めました{addText}");
        return;
    }

    // 会計年度の解決と締め済み期間ガード（境界日知見: 月末日は辞書順比較で失敗するため月初日で解決）
    // 計上日の期間が締め済み（または期間未設定）なら処理日（今日）へ自動フォールバックする
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
            Toaster.Error("計上日の期間に起票できず、本日の期間も締め済みまたは未設定です。会計年度・月次期間の設定を確認してください");
            return;
        }
        if (typedFy != null && $"{fyToday.Id.Value}" != $"{typedFy.Id.Value}")
        {
            Toaster.Warn($"計上日（{ExpenseDate.Value:yyyy/MM/dd}）は前年度です。当期の費用として計上します。金額が重要な場合は決算修正をご検討ください");
        }
        entryDate = today;
        typedFy = fyToday;
        typedPeriod = periodToday;
        usedFallback = true;
    }

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

    // 固定資産計上の行があるときだけ工具器具備品(1520)を解決する
    object assetAccountId = null;
    var hasAssetLine = false;
    foreach (var l in lines) { if (l.IsFixedAsset.Value == true) hasAssetLine = true; }
    if (hasAssetLine)
    {
        var accS = new ModuleSearcher<Account>();
        accS.AddEquals(e => e.Code.Value, "1520");
        var assetAcc = accS.ExecuteFirstOrDefault();
        if (assetAcc == null) { Toaster.Error("工具器具備品(1520)の科目がありません"); return; }
        // この科目はそのまま固定資産台帳の計上科目になる。固定資産科目でないと償却仕訳を
        // 生成できない（ADR-0063 の関門で止まる）ので、先にここで気づかせる
        if (((Account)assetAcc).IsFixedAssetAccount.Value != true)
        {
            Toaster.Error("工具器具備品(1520)が「固定資産科目」になっていません。科目マスタを確認してください");
            return;
        }
        assetAccountId = ((Account)assetAcc).Id.Value;
    }

    // 申請者の所属部門を解決して全行に引き継ぐ（部門別予実に乗せる。B-7）
    // 注: レイアウトに出ていない値は遅延ロードで null のことがある (#60) → DB から取り直す
    var creatorId = Creator.Value;
    if (creatorId == null)
    {
        var es = new ModuleSearcher<ExpenseRequestAccounting>();
        es.AddEquals(e => e.Id.Value, this.Id.Value);
        var self = es.ExecuteFirstOrDefault();
        if (self != null) { creatorId = ((ExpenseRequestAccounting)self).Creator.Value; }
    }
    object creatorDeptId = null;
    if (creatorId != null)
    {
        var us = new ModuleSearcher<AppUser>();
        us.AddEquals(u => u.Id.Value, creatorId);
        var creatorUser = us.ExecuteFirstOrDefault();
        if (creatorUser != null) { creatorDeptId = ((AppUser)creatorUser).所属部.Value; }
    }

    // ---- 明細から仕訳行を組み立てる ----
    // 借方は明細 1 行につき 1 行（同じ科目でも畳まない。総勘定元帳で明細が追えるほうが実務で役に立つ）。
    // 税行は借方行ごと（レシート記載の税額が正なので、1 枚ごとの税額をそのまま記帳する）。
    // 貸方（未払金）は支払先がヘッダで 1 つなので 1 行にまとめる。
    var dcList = new List<string>();
    var accList = new List<object>();
    var taxCatList = new List<object>();
    var taxModeList = new List<string>();
    var amtList = new List<int>();
    var inAmtList = new List<int>();
    var descList = new List<string>();
    var prjList = new List<object>();
    var isTaxList = new List<bool>();
    var parentNoList = new List<int>();

    var total = 0;
    var lineNo = 0;
    var summaryName = "";
    foreach (var l in lines)
    {
        var cat = FindCategory(l.ExpenseCategoryRef.Value);
        if (cat == null) { Toaster.Error($"{lineNo + 1} 行目: 費目が解決できません"); return; }
        var debitAccountId = cat.DefaultAccount.Value;
        if (l.IsFixedAsset.Value == true) { debitAccountId = assetAccountId; }
        if (debitAccountId == null) { Toaster.Error($"費目「{cat.Name.Value}」に既定勘定科目が設定されていません"); return; }

        var tcat = ResolveLineTaxCategory(l, cat);
        object taxCatId = null;
        if (tcat != null) taxCatId = tcat.Id.Value;
        if (taxCatId == null) { Toaster.Error($"{lineNo + 1} 行目: 税区分が解決できません（費目の既定税区分を確認してください）"); return; }

        int gross = l.Amount.Value ?? 0;
        int tax = CalcLineTax(l);
        int baseAmount = gross - tax;
        total = total + gross;

        var desc = l.Description.Value;
        if (string.IsNullOrEmpty(desc)) desc = $"{Title.Value}";
        if (!string.IsNullOrEmpty(l.UsedAt.Value)) desc = $"{desc}（{l.UsedAt.Value}）";

        lineNo = lineNo + 1;
        var debitNo = lineNo;
        dcList.Add("D");
        accList.Add(debitAccountId);
        taxCatList.Add(taxCatId);
        taxModeList.Add("inclusive");
        amtList.Add(baseAmount);
        inAmtList.Add(gross);
        descList.Add(desc);
        prjList.Add(l.ProjectRef.Value);
        isTaxList.Add(false);
        parentNoList.Add(0);

        if (tax > 0)
        {
            lineNo = lineNo + 1;
            dcList.Add("D");
            accList.Add(purchaseTaxAccountId);
            taxCatList.Add(taxCatId);
            taxModeList.Add("none");
            amtList.Add(tax);
            inAmtList.Add(tax);
            descList.Add($"消費税（行{debitNo}）");
            prjList.Add(l.ProjectRef.Value);
            isTaxList.Add(true);
            parentNoList.Add(debitNo);
        }

        if (summaryName == "")
        {
            var accName = ResolveAccountName(debitAccountId);
            summaryName = accName;
            if (accName != $"{cat.Name.Value}") summaryName = $"{accName}（費目: {cat.Name.Value}）";
        }
    }

    if (total <= 0) { Toaster.Error("金額が入力されていません"); return; }
    if (purchaseTaxAccountId == null)
    {
        foreach (var b in isTaxList) { if (b) { Toaster.Error("仮払消費税(1900)の科目がありません"); return; } }
    }

    lineNo = lineNo + 1;
    dcList.Add("C");
    accList.Add(apAccountId);
    taxCatList.Add(null);
    taxModeList.Add("none");
    amtList.Add(total);
    inAmtList.Add(total);
    descList.Add($"{Title.Value}");
    prjList.Add(null);
    isTaxList.Add(false);
    parentNoList.Add(0);

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

    var je = new JournalEntry();
    je.EntryDate.Value = entryDate;
    je.EntryType.Value = "auto";
    if (usedFallback) { je.Description.Value = $"経費精算 {Title.Value}（計上日 {ExpenseDate.Value:yyyy/MM/dd}）"; }
    else { je.Description.Value = $"経費精算 {Title.Value}"; }
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "expense";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(dcList.Count);
    var idx = -1;
    foreach (var row in je.Lines.Rows)
    {
        var jl = (JournalLine)row;
        idx = idx + 1;
        jl.LineNo.Value = idx + 1;
        jl.Dc.Value = dcList[idx];
        jl.Account.Value = accList[idx];
        jl.TaxInputMode.Value = taxModeList[idx];
        jl.Amount.Value = amtList[idx];
        jl.InputAmount.Value = inAmtList[idx];
        jl.Description.Value = descList[idx];
        if (taxCatList[idx] != null) { jl.TaxCategory.Value = taxCatList[idx]; }
        if (creatorDeptId != null) { jl.Department.Value = creatorDeptId; }
        if (prjList[idx] != null) { jl.ProjectRef.Value = prjList[idx]; }
        if (isTaxList[idx])
        {
            jl.IsTaxLine.Value = true;
            jl.ParentLineNo.Value = parentNoList[idx];
        }
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("仕訳の生成に失敗しました"); return; }

    // 固定資産計上対象の行を台帳へ自動登録（取得価額は税抜本体額。部門は仕訳と同じ）
    var assetNo = 0;
    var registered = 0;
    foreach (var l in lines)
    {
        assetNo = assetNo + 1;
        if (l.IsFixedAsset.Value != true) continue;
        int gross = l.Amount.Value ?? 0;
        int baseAmount = gross - CalcLineTax(l);
        // 取得日は**仕訳と同じ日**を使う（BUG-0318）。行の利用日をそのまま入れると、
        // 計上日が締め済みで仕訳を当日へ逃がしたときに、台帳だけ締めた月を起点に償却が始まる
        if (RegisterFixedAsset(l, assetNo, assetAccountId, baseAmount, creatorDeptId, entryDate)) registered = registered + 1;
    }

    SettlementStatus.Value = "accounting";
    var ret2 = this.Submit();
    if (ret2 == false) { Toaster.Error("精算ステータスの更新に失敗しました"); return; }
    UpdateAccountingButtons();
    Toaster.Success($"仕訳 No.{nextNo} を生成しました（明細 {lines.Count} 行 / 借方 {summaryName} ほか / 貸方 未払金 {total:#,0} 円）");
    if (registered > 0)
    {
        Toaster.Info($"固定資産台帳に {registered} 件を登録しました（耐用年数・償却方法は台帳で確定してください）");
    }
    if (usedFallback)
    {
        Toaster.Info($"計上日（{ExpenseDate.Value:yyyy/MM/dd}）の期間が締め済みのため、本日（{entryDate:yyyy/MM/dd}）日付で起票しました（摘要に計上日を記載）");
    }
}

// ============================================================
// 経理: 精算済にする (accounting → settled)
// 支払仕訳 (D 未払金2020 / C 普通預金1020) を生成してからステータスを進める
// ============================================================
void Settle_OnClick()
{
    if (SettlementStatus.Value != "accounting") return;
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("金額が入力されていません"); return; }

    // 支払仕訳を作る操作で、この画面に取り消す導線が無い＝不可逆なので確認する（ADR-0062）
    var answer = MessageBox.Show(
        "この申請を精算済みにし、支払仕訳を生成します。取り消す導線はこの画面にありません"
        + "（誤りは振替伝票側で訂正してください）。よろしいですか？",
        "精算する", "キャンセル");
    if (answer != "精算する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード。仕訳生成側と同じく、**支払仕訳はあるのに状態が accounting のまま**なら
    // 中断状態なので、作り直さずに状態だけ進めて自己修復する（BUG-0311）
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "expense_payment");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var existingPay = js.Execute();
    if (existingPay.Count > 0)
    {
        SettlementStatus.Value = "settled";
        var retFix = this.Submit();
        if (retFix != true)
        {
            Toaster.Error("この申請の支払仕訳は既に生成済みですが、精算ステータスの更新に失敗しました。画面を開き直してもう一度お試しください");
            return;
        }
        UpdateAccountingButtons();
        var fixedNo = ((JournalEntry)existingPay[0]).JournalNo.Value;
        Toaster.Info($"支払仕訳 No.{fixedNo} は既に生成されていました。二重には作らず、精算ステータスだけ「精算済」に進めました");
        return;
    }

    // 支払日=今日。会計年度・期間の解決 (境界日知見: 期間解決はその月の月初日で行う)
    var payDate = DateOnly.FromDateTime(DateTime.Today);
    var typedFy = ResolveYearForDate(payDate);
    if (typedFy == null) { Toaster.Error("支払日に対応する会計年度がありません"); return; }
    var typedPeriod = ResolvePeriodForDate(payDate);
    if (typedPeriod == null) { Toaster.Error("支払日に対応する月次期間がありません"); return; }
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
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
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

// ============================================================
// 明細・マスタの解決ヘルパー（申請者用 ExpenseRequest と同じ規約。型だけ経理用に差し替えたもの）
// ============================================================

// 行の税額: 行の税区分が課税仕入のときだけ。レシート記載（手入力）を優先し、無ければ内税計算（切り捨て）
int CalcLineTax(ExpenseRequestLineAccounting l)
{
    if (l == null) return 0;
    if (l.Amount.Value == null || l.Amount.Value <= 0) return 0;
    var tcat = ResolveLineTaxCategory(l, FindCategory(l.ExpenseCategoryRef.Value));
    if (!IsTaxablePurchaseTaxCategory(tcat)) return 0;
    if (l.TaxAmount.Value != null && l.TaxAmount.Value > 0) return l.TaxAmount.Value;
    decimal pct = GetTaxRatePercent(tcat);
    if (pct == 0) return 0;
    int gross = l.Amount.Value;
    int tax = gross * pct / (100 + pct);
    return tax;
}

// 行の税区分: 行に設定があればそれ、無ければ費目の既定
TaxCategory ResolveLineTaxCategory(ExpenseRequestLineAccounting l, ExpenseCategory cat)
{
    object id = null;
    if (l != null && l.TaxCategoryRef.Value != null) id = l.TaxCategoryRef.Value;
    else if (cat != null) id = cat.DefaultTaxCategory.Value;
    if (id == null) return null;
    var s = new ModuleSearcher<TaxCategory>();
    s.AddEquals(c => c.Id.Value, id);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (TaxCategory)found;
}

bool IsTaxablePurchaseTaxCategory(TaxCategory tcat)
{
    if (tcat == null) return false;
    return (tcat.TaxationType.Value == "taxable_purchase");
}

// 費目マスタの取得（未選択・解決不能なら null）
ExpenseCategory FindCategory(object categoryId)
{
    if (categoryId == null) return null;
    var s = new ModuleSearcher<ExpenseCategory>();
    s.AddEquals(c => c.Id.Value, categoryId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (ExpenseCategory)found;
}

// 税区分に紐づく税率(%)。未設定・解決不能なら 0
decimal GetTaxRatePercent(TaxCategory tcat)
{
    if (tcat == null) return 0;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    return ((TaxRate)foundRate).RatePercent.Value ?? 0;
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

// 勘定科目名（解決できなければ空文字）
string ResolveAccountName(object accountId)
{
    if (accountId == null) return "";
    var s = new ModuleSearcher<Account>();
    s.AddEquals(e => e.Id.Value, accountId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return "";
    return $"{((Account)found).Name.Value}";
}

// 固定資産台帳への自動登録（償却方法は仮=定額法。耐用年数と方法は経理が台帳で確定する）
bool RegisterFixedAsset(ExpenseRequestLineAccounting l, int lineNo, object assetAccountId, int baseAmount, object departmentId, var acquisitionDate)
{
    var code = l.AssetNo.Value;
    if (code == null || code == "") { code = $"EXP-{this.Id.Value}-{lineNo}"; }
    var fs = new ModuleSearcher<FixedAsset>();
    fs.AddEquals(f => f.Code.Value, code);
    if (fs.Execute().Count > 0)
    {
        // 同じ管理番号が既にあるときは黙って飛ばさない（BUG-0310）。
        // 2 行に同じ番号を手入力すると仕訳は 2 行とも資産計上されるのに台帳は 1 件だけ、という
        // 食い違いが**何の表示も無いまま**残っていた
        Toaster.Error($"{lineNo} 行目: 資産管理番号「{code}」は既に固定資産台帳にあります。番号を直すか、台帳側で確認してください（この行は台帳に登録していません）");
        return false;
    }

    var name = l.Description.Value;
    if (string.IsNullOrEmpty(name)) name = $"{Title.Value}";

    var fa = new FixedAsset();
    fa.Code.Value = code;
    fa.Name.Value = name;
    if (departmentId != null) { fa.Department.Value = departmentId; }
    fa.AssetAccount.Value = assetAccountId;
    fa.AcquisitionDate.Value = acquisitionDate;
    fa.AcquisitionCost.Value = baseAmount;
    fa.DepreciationMethod.Value = "straight_line";
    fa.Status.Value = "in_use";
    fa.Memo.Value = $"経費申請「{Title.Value}」{lineNo} 行目から自動登録。耐用年数・償却方法を確認してください";
    var ret = fa.Submit();
    if (ret != true) { Toaster.Error($"固定資産台帳への自動登録に失敗しました（{code}）。手動で登録してください"); return false; }
    return true;
}
