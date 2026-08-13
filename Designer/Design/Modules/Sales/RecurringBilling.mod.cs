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
    return Creator.Value == CurrentUser.Id.Value;
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
        msg = $"この契約を終了にします。終了月が未設定のため {thisMonth:yyyy年M月} を終了月として設定します（「定期請求の実行」の対象から外れます）。よろしいですか？";
    }
    else
    {
        msg = $"この契約を終了にします（終了月: {EndMonth.Value:yyyy年M月}）。「定期請求の実行」の対象から外れます。よろしいですか？";
    }
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

    var answer = MessageBox.Show("この契約の終了を解除して確定済に戻します（「定期請求の実行」の対象に戻ります。終了していた期間のうち未生成の月も対象になります）。よろしいですか？", "終了を解除", "キャンセル");
    if (answer != "終了を解除") return;

    using var loading = LoadingService.StartLoading(1000);
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
