// RecurringBilling.mod.cs — 定期請求契約
// 状態フロー: draft（下書き）→ confirmed（確定済）→ ended（終了）。確定・差し戻し・終了・
// 終了の解除・有効/無効の切替は経理のみ。下書きの削除は作成者本人＋経理（ADR-0057）。
// 実行対象は「確定済かつ有効」（RecurringRun 側で絞る）。有効チェックは確定後の一時停止スイッチ。
// 「終了」は end_month を唯一の真実の源に保つため、必ず終了月の設定を伴う（ADR-0057）。
// 権限マトリクス: 一般/承認者=閲覧＋下書きの作成・編集、経理/sysadmin=全操作（docs/tests/16 §0.3）

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "draft";
        // 有効は初期 ON（実行対象は「確定済かつ有効」なので下書きの間は実害がなく、
        // 確定した瞬間に課金が始まる=最も普通のケースをワンクリックにする。ユーザー決定 2026-07-23）
        IsActive.Value = true;
        if (BillingCycle.Value == null || BillingCycle.Value == "") { BillingCycle.Value = "monthly"; }
        // 部門の初期値: 作成者の所属部（主所属が課でも伝票部門は部・ADR-0044。スナップショット思想）
        if (DepartmentRef.Value == null) { DepartmentRef.Value = CurrentUser.所属部.Value; }
    }
    UpdateUi();
}

// 一覧の既定絞り込み: 終了した契約は既定では出さない（ADR-0057・改善候補 C-9）。
// 終了は end_month から導出できる事実だが、CLB の検索フォームでは
// 「終了月が空欄 または 今月以降」という NULL を含む OR 条件を書けないため（AllowEmptySearch を
// 実測して確認済み）、状態として持たせたうえでここで既定値を入れる。
// サイドバー経由（?initialize_search=true 付き）でのみ発火する
void Search_OnSearchInitialization()
{
    var vals = new List<string>();
    vals.Add("draft");
    vals.Add("confirmed");
    Status.SearchValues = vals;
}

void BillingCycle_OnDataChanged()
{
    UpdateCycleVisibility();
}

// 課金サイクルに応じて月額欄／年額欄を出し分ける（非表示側の値はクリアしない。
// 実行側はサイクルに該当する金額しか読まないため、誤切替で入力値を失わない方を優先）
void UpdateCycleVisibility()
{
    var isYearly = BillingCycle.Value == "yearly";
    MonthlyAmountLabel.IsVisible = !isYearly;
    MonthlyAmount.IsVisible = !isYearly;
    AnnualAmountLabel.IsVisible = isYearly;
    AnnualAmount.IsVisible = isYearly;
}

void UpdateUi()
{
    UpdateCycleVisibility();

    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
    var st = Status.Value;
    // 確定済・終了は経理以外に読み取り専用（サーバ側の行ロックまでは行わない=受注ロックと同じ割り切り）。
    // 終了を含めるのが要点——含めないと「終了にする」で確定ロックが外れてしまう（ADR-0057）
    var locked = (st == "confirmed" || st == "ended") && !isAccounting;

    if (locked)
    {
        this.IsViewOnly = true;
    }
    LockedNoteLabel.IsVisible = locked;
    if (st == "ended")
    {
        LockedNoteLabel.Text = "終了した契約です。内容の変更・終了の解除は経理が行います";
    }
    else
    {
        LockedNoteLabel.Text = "確定済の契約です。内容の変更・有効/無効の切替は経理が行います";
    }
    SubmitButton.IsVisible = !locked;

    // 有効チェックは経理のみ操作可（確定後の一時停止スイッチ）
    if (!isAccounting) { IsActive.IsViewOnly = true; }

    // 部門は経理のみ変更可（2026-07-25 ユーザー要望）。一般・承認者は自部門（初期値）固定
    if (!isAccounting) { DepartmentRef.IsViewOnly = true; }

    // 状態遷移ボタン（ADR-0026: 状態変更はボタン経由に一本化。ADR-0027: 青=前進・赤=巻き戻し/削除）
    ConfirmButton.IsVisible = isAccounting && st == "draft";
    EndButton.IsVisible = isAccounting && st == "confirmed";
    RevertButton.IsVisible = isAccounting && st == "confirmed";
    UnendButton.IsVisible = isAccounting && st == "ended";
    DeleteContractButton.IsVisible = !this.IsNewData && st == "draft" && CanDeleteContract();
    ConfirmButton.IsViewOnly = false;
    EndButton.IsViewOnly = false;
    RevertButton.IsViewOnly = false;
    UnendButton.IsViewOnly = false;
    DeleteContractButton.IsViewOnly = false;
}

// ============ 請求実績の判定（巻き戻し・削除のガード） ============

// この契約から生成された請求書の件数（取消 void も数える）。削除の可否に使う——
// invoices.recurring_billing_id の外部キーがあるため、1 件でも残っていれば物理削除は失敗する
int CountInvoices()
{
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.RecurringBillingRef.Value, this.Id.Value);
    return s.Execute().Count;
}

// 取消（void）でない請求書の件数。RecurringRun は void を「未請求」とみなして再生成の対象に
// 戻すため、下書きへの巻き戻しの可否も同じ流儀で判定する
int CountLiveInvoices()
{
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.RecurringBillingRef.Value, this.Id.Value);
    var n = 0;
    foreach (var row in s.Execute())
    {
        var inv = (Invoice)row;
        if (inv.Status.Value != "void") { n = n + 1; }
    }
    return n;
}

// 下書きの削除は作成者本人と経理（ユーザー決定 2026-08-14）。
// 下書きは一般ユーザーも作れるため、自分が作ったものは自分で片付けられるようにする
bool CanDeleteContract()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return true;
    // **生の `==` で id を比べない**（BUG-0399 と同型）。動的型の比較は型が違うと黙って false になり、
    // 「自分が作った下書きなのに削除ボタンが出ない」という静かな失敗になる
    return $"{Creator.Value}" == $"{CurrentUser.Id.Value}";
}

void Confirm_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true) { Toaster.Error("契約の確定は経理のみ行えます"); return; }
    if (Status.Value != "draft") { Toaster.Error("下書きの契約のみ確定できます"); return; }

    // 確定時の入力チェック（確定後は実行対象になるため、金額の欠落をここで止める）
    if (BillingCycle.Value == "monthly" && (MonthlyAmount.Value == null || MonthlyAmount.Value <= 0))
    {
        Toaster.Error("月額（税抜）を入力してから確定してください");
        return;
    }
    if (BillingCycle.Value == "yearly" && (AnnualAmount.Value == null || AnnualAmount.Value <= 0))
    {
        Toaster.Error("年額（税抜）を入力してから確定してください");
        return;
    }
    if (!this.ValidateInput()) { return; }

    var answer = MessageBox.Show("この契約を確定します（「定期請求の実行」の対象になります）。よろしいですか？", "確定する", "キャンセル");
    if (answer != "確定する") return;

    using var loading = LoadingService.StartLoading(1000);
    Status.Value = "confirmed";
    var ok = this.Submit();
    if (ok != true)
    {
        Status.Value = "draft";
        Toaster.Error("確定に失敗しました");
        UpdateUi();
        return;
    }
    UpdateUi();
    Toaster.Success("契約を確定しました");
}

// 下書きに戻す: confirmed → draft。請求実績がある契約は戻せない（ADR-0057・改善候補 C-8）。
// 下書きは一般ユーザーも編集できるため、戻せてしまうと「確定済の変更は経理のみ」という
// 権限がボタン 1 つで抜けられる。実行対象から外すだけなら「有効」チェックが本来の手段
void Revert_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true) { Toaster.Error("下書きへの差し戻しは経理のみ行えます"); return; }
    if (Status.Value != "confirmed") { Toaster.Error("確定済の契約のみ下書きに戻せます"); return; }

    var live = CountLiveInvoices();
    if (live > 0)
    {
        Toaster.Error($"この契約から請求書が {live} 件発行されているため下書きに戻せません（請求を止めるだけなら「有効」のチェックを外してください）");
        return;
    }

    var answer = MessageBox.Show("この契約を下書きに戻します（「定期請求の実行」の対象から外れます）。よろしいですか？", "下書きに戻す", "キャンセル");
    if (answer != "下書きに戻す") return;

    using var loading = LoadingService.StartLoading(1000);
    Status.Value = "draft";
    var ok = this.Submit();
    if (ok != true)
    {
        Status.Value = "confirmed";
        Toaster.Error("差し戻しに失敗しました");
        UpdateUi();
        return;
    }
    UpdateUi();
    Toaster.Success("契約を下書きに戻しました");
}

// 終了にする: confirmed → ended（ADR-0057・改善候補 C-9）。
// 終了月を必ず埋める——「終了しているか」の真実の源は end_month であり、
// 状態がそれと食い違わないようにする（RecurringRun は end_month で対象月を判定する）
void End_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true) { Toaster.Error("契約の終了は経理のみ行えます"); return; }
    if (Status.Value != "confirmed") { Toaster.Error("確定済の契約のみ終了にできます"); return; }

    var today = DateOnly.FromDateTime(DateTime.Today);
    var thisMonth = new DateOnly(today.Year, today.Month, 1);
    var msg = "";
    if (EndMonth.Value == null)
    {
        msg = $"この契約を終了にします。終了月が未設定のため {thisMonth:yyyy年M月} を終了月として設定します（「定期請求の実行」の対象から外れます）。";
    }
    else
    {
        msg = $"この契約を終了にします（終了月: {EndMonth.Value:yyyy年M月}）。「定期請求の実行」の対象から外れます。";
    }
    // 年額契約は按分の途中で終わるので、**残った前受収益をここで売上に落とす**（BUG-0146）。
    // 終了してから按分が止まることを利用者は知らないので、金額を見せてから確認を取る
    var deferred = DeferredBalance();
    if (deferred > 0)
    {
        // MessageBox は素のテキスト。**強調記号は出しても記号のまま表示される**ので使わない
        msg = msg + $"あわせて、前受収益の未償却残 {deferred:#,0} 円 を終了月の売上に一括計上します"
            + "（借方 前受収益 / 貸方 SaaS売上高）。終了を解除すればこの仕訳も取り消されます。";
    }
    msg = msg + "よろしいですか？";
    var answer = MessageBox.Show(msg, "終了にする", "キャンセル");
    if (answer != "終了にする") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(1000);
    var prevEnd = EndMonth.Value;
    if (EndMonth.Value == null) { EndMonth.Value = thisMonth; }
    Status.Value = "ended";
    var ok = this.Submit();
    if (ok != true)
    {
        Status.Value = "confirmed";
        EndMonth.Value = prevEnd;
        Toaster.Error("終了に失敗しました");
        UpdateUi();
        return;
    }
    // 前受収益の打ち切り。**失敗しても終了自体は戻さない**——終了は業務上の事実で、
    // 起票できない理由（締め済み等）は利用者に伝えて手動起票してもらうほうが素直
    if (deferred > 0) { PostDeferredSettlement(deferred, EndMonth.Value); }

    UpdateUi();
    Toaster.Success($"契約を終了にしました（終了月: {EndMonth.Value:yyyy年M月}）");
}

// 終了を解除: ended → confirmed。終了月は勝手に書き換えない——
// 過去のままだと RecurringRun が対象月の判定で毎月スキップし続ける（＝解除したのに請求が
// 始まらない無言の失敗）ので、警告して利用者に手当てを促す
void Unend_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true) { Toaster.Error("終了の解除は経理のみ行えます"); return; }
    if (Status.Value != "ended") { Toaster.Error("終了した契約のみ解除できます"); return; }

    var settleJe = FindSettleJournal();
    var settleNote = (settleJe == null) ? ""
        : $"前受収益の打ち切り仕訳 No.{settleJe.JournalNo.Value} も取り消され、未償却残が元に戻ります。";
    var answer = MessageBox.Show("この契約の終了を解除して確定済に戻します（「定期請求の実行」の対象に戻ります。終了していた期間のうち未生成の月も対象になります）。"
        + settleNote + "よろしいですか？", "終了を解除", "キャンセル");
    if (answer != "終了を解除") return;

    using var loading = LoadingService.StartLoading(1000);
    // 打ち切り仕訳を先に戻す。戻せない（締め済み）なら解除自体を中止する——
    // 解除だけ通すと、按分が再開したうえに打ち切り分も残って**売上が二重に立つ**
    if (!DeleteSettlementJournal()) { return; }
    Status.Value = "confirmed";
    var ok = this.Submit();
    if (ok != true)
    {
        Status.Value = "ended";
        Toaster.Error("終了の解除に失敗しました");
        UpdateUi();
        return;
    }
    UpdateUi();
    Toaster.Success("契約の終了を解除しました");

    if (EndMonth.Value != null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var em = EndMonth.Value;
        var endFirst = new DateOnly(em.Year, em.Month, 1);
        if (endFirst < thisMonth)
        {
            Toaster.Warn($"終了月（{EndMonth.Value:yyyy年M月}）が過去のままです。請求を再開するには終了月を空欄にするか、先の月に変更してください");
        }
    }
}

// 契約の削除: 下書きのみ・請求実績が無いときのみ（ADR-0057・改善候補 C-8）。
// 確定済から下書きに戻せる以上「請求実績のある下書き」もありえたため、状態だけでは足りない
void DeleteContract_OnClick()
{
    if (Status.Value != "draft") { Toaster.Error("下書きの契約のみ削除できます"); return; }
    if (!CanDeleteContract()) { Toaster.Error("この契約を削除できるのは作成者本人と経理です"); return; }

    var n = CountInvoices();
    if (n > 0)
    {
        Toaster.Error($"この契約から請求書が {n} 件作成されているため削除できません（取消にした請求書も履歴として残ります）");
        return;
    }

    var result = MessageBox.Show($"定期請求契約「{Title.Value}」を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;

    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("定期請求契約を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("RecurringBilling"));
}

// ───────────────────────────────────────────────────────────────────────────
// 前受収益の打ち切り（BUG-0146・ADR-0071 の帰結として開発者が決めた方針）
//
// 年額契約は「借方 売掛金 / 貸方 前受収益」で一括計上し、毎月「借方 前受収益 / 貸方 SaaS売上高」で
// 按分して収益にしていく。ところが**契約が終了した瞬間に按分が止まる**（`RecurringRun` が
// 対象契約から外すため）ので、**残った前受収益を戻す経路がどこにも無かった**。
// B/S に前受収益が塩漬けになり、P/L に売上が計上されないまま永久に残る。
//
// 方針（開発者決定）: **月単位で打ち切り、残額は解約月の売上に一括計上する。**
// 終了ボタンで按分の残りを 1 本の仕訳（借方 前受収益 / 貸方 SaaS売上高）にして落とす。
// 終了を解除したらその仕訳も消して元に戻す（ADR-0070 の「締めるまでは打ち直せる」）。
// ───────────────────────────────────────────────────────────────────────────

// この契約に紐づく前受収益(2110)の未償却残。
//   前受計上（recurring_annual の貸方） − 按分振替（recurring_defer の借方） − 打ち切り（recurring_settle の借方）
int DeferredBalance()
{
    if (this.Id.Value == null) return 0;
    object deferredId = null;
    var accS = new ModuleSearcher<Account>();
    accS.AddEquals(e => e.Code.Value, "2110");
    var accFound = accS.ExecuteFirstOrDefault();
    if (accFound == null) return 0;
    deferredId = ((Account)accFound).Id.Value;

    // この契約の請求書 id を集める
    var invIds = new List<string>();
    var invS = new ModuleSearcher<Invoice>();
    invS.AddEquals(e => e.RecurringBillingRef.Value, this.Id.Value);
    foreach (var row in invS.Execute()) { invIds.Add($"{((Invoice)row).Id.Value}"); }

    var total = 0;
    var jS = new ModuleSearcher<JournalEntry>();
    jS.AddIn(e => e.SourceType.Value, "recurring_annual", "recurring_defer", "recurring_settle");
    jS.AddEquals(e => e.Status.Value, "posted");
    foreach (var row in jS.Execute())
    {
        var je = (JournalEntry)row;
        var mine = false;
        if (je.SourceType.Value == "recurring_settle")
        {
            mine = ($"{je.SourceId.Value}" == $"{this.Id.Value}");
        }
        else
        {
            foreach (var iid in invIds) { if (iid == $"{je.SourceId.Value}") { mine = true; break; } }
        }
        if (!mine) continue;
        var ls = new ModuleSearcher<JournalLine>();
        ls.AddEquals(l => l.JournalEntryId.Value, je.Id.Value);
        foreach (var lrow in ls.Execute())
        {
            var l = (JournalLine)lrow;
            if ($"{l.Account.Value}" != $"{deferredId}") continue;
            if (l.Amount.Value == null) continue;
            if (l.Dc.Value == "C") { total = total + l.Amount.Value; }
            else { total = total - l.Amount.Value; }
        }
    }
    if (total < 0) return 0;
    return total;
}

JournalEntry FindSettleJournal()
{
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.SourceType.Value, "recurring_settle");
    s.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (JournalEntry)found;
}

// 解約月の月末に「借方 前受収益 / 貸方 SaaS売上高」を 1 本起票する
bool PostDeferredSettlement(int amount, var endMonthFirst)
{
    if (amount <= 0) return true;
    var monthEnd = endMonthFirst.AddMonths(1).AddDays(-1);

    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, endMonthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, endMonthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("終了月に対応する会計年度がありません。前受収益の打ち切りを起票できません"); return false; }
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, endMonthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, endMonthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("終了月に対応する月次期間がありません。前受収益の打ち切りを起票できません"); return false; }
    if (((FiscalPeriod)period).Status.Value == "closed")
    {
        Toaster.Error("終了月の期間が締め済みです。前受収益の打ち切りは振替伝票で手動起票してください（借方 前受収益 / 貸方 SaaS売上高）");
        return false;
    }

    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "2110", "4020");
    object deferredId = null;
    object saasId = null;
    foreach (var a in accS.Execute())
    {
        var acc = (Account)a;
        if (acc.Code.Value == "2110") { deferredId = acc.Id.Value; }
        if (acc.Code.Value == "4020") { saasId = acc.Id.Value; }
    }
    if (deferredId == null || saasId == null) { Toaster.Error("打ち切りに必要な科目（2110/4020）がありません"); return false; }

    // 行の内容はプリミティブの並行リストで組む（CLB-039・ISSUE-0006）
    var dcList = new List<string>();
    var accList = new List<object>();
    dcList.Add("D"); accList.Add(deferredId);
    dcList.Add("C"); accList.Add(saasId);

    var nextNo = new JournalEntry().NextJournalNo(((FiscalYear)fy).Id.Value);
    var je = new JournalEntry();
    je.EntryDate.Value = monthEnd;
    je.EntryType.Value = "adjust";
    je.Description.Value = $"前受収益の打ち切り {Title.Value}（契約終了・{endMonthFirst:yyyy年M月}）";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = ((FiscalYear)fy).Id.Value;
    je.SourceType.Value = "recurring_settle";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(dcList.Count);
    var i = 0;
    foreach (var lr in je.Lines.Rows)
    {
        var l = (JournalLine)lr;
        l.LineNo.Value = i + 1;
        l.Dc.Value = dcList[i];
        l.Account.Value = accList[i];
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        l.TaxInputMode.Value = "none";
        l.Description.Value = $"前受収益の打ち切り {Title.Value}";
        if (ProjectRef.Value != null) { l.ProjectRef.Value = ProjectRef.Value; }
        if (DepartmentRef.Value != null) { l.Department.Value = DepartmentRef.Value; }
        i = i + 1;
    }
    // 内部振替なので消費税の対象外（ADR-0053）
    je.MarkAllLinesOutOfScope();
    je.FillMissingDepartments();
    // 貸借一致の検証（BUG-0068）。**Submit の前**に見るので、止めれば伝票は生まれない
    var imbalance = je.ValidateBalanced();
    if (imbalance != "")
    {
        Toaster.Error($"前受収益の打ち切り仕訳の生成を中止しました（{imbalance}）");
        return false;
    }
    if (je.Submit() != true) { Toaster.Error("前受収益の打ち切り仕訳の生成に失敗しました"); return false; }
    Toaster.Info($"前受収益の未償却残 {amount:#,0} 円を {endMonthFirst:yyyy年M月} の売上に振り替えました（伝票 No.{nextNo}）");
    return true;
}

// 終了の解除で打ち切り仕訳を戻す。締め済みなら戻さず、理由を伝えて終了解除自体を止める
bool DeleteSettlementJournal()
{
    var je = FindSettleJournal();
    if (je == null) return true;
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
            Toaster.Error($"前受収益の打ち切り仕訳 No.{je.JournalNo.Value} の期間が締め済みです。終了を解除するとその仕訳だけ残って前受収益が二重に戻るため、中止しました（赤伝で打ち消してから解除してください）");
            return false;
        }
    }
    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(l => l.JournalEntryId.Value, je.Id.Value);
    foreach (var row in ls.Execute())
    {
        var l = (JournalLine)row;
        if (l.Delete() != true) { Toaster.Error("打ち切り仕訳の明細削除に失敗しました"); return false; }
    }
    if (je.Delete() != true) { Toaster.Error("打ち切り仕訳の削除に失敗しました"); return false; }
    Toaster.Info($"前受収益の打ち切り仕訳 No.{je.JournalNo.Value} を削除しました");
    return true;
}
