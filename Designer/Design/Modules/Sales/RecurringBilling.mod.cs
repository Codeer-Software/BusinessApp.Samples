// RecurringBilling.mod.cs — 定期請求契約
// 状態フロー: draft（下書き）→ confirmed（確定済）。確定・差し戻し・有効/無効の切替は経理のみ。
// 実行対象は「確定済かつ有効」（RecurringRun 側で絞る）。有効チェックは確定後の一時停止スイッチ。
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
        // 部門の初期値: 作成者の所属部門（スナップショット思想。ddl/330・経費申請と同じ）
        if (DepartmentRef.Value == null) { DepartmentRef.Value = CurrentUser.所属部門.Value; }
    }
    UpdateUi();
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
    var locked = st == "confirmed" && !isAccounting;

    // 確定済は経理以外に読み取り専用（サーバ側の行ロックまでは行わない=受注ロックと同じ割り切り）
    if (locked)
    {
        this.IsViewOnly = true;
    }
    LockedNoteLabel.IsVisible = locked;
    SubmitButton.IsVisible = !locked;

    // 有効チェックは経理のみ操作可（確定後の一時停止スイッチ）
    if (!isAccounting) { IsActive.IsViewOnly = true; }

    // 部門は経理のみ変更可（2026-07-25 ユーザー要望）。一般・承認者は自部門（初期値）固定
    if (!isAccounting) { DepartmentRef.IsViewOnly = true; }

    // 状態遷移ボタン（経理のみ。ADR-0026: 状態変更はボタン経由に一本化）
    ConfirmButton.IsVisible = isAccounting && st == "draft";
    RevertButton.IsVisible = isAccounting && st == "confirmed";
    ConfirmButton.IsViewOnly = false;
    RevertButton.IsViewOnly = false;
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

void Revert_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true) { Toaster.Error("下書きへの差し戻しは経理のみ行えます"); return; }
    if (Status.Value != "confirmed") { Toaster.Error("確定済の契約のみ下書きに戻せます"); return; }

    var answer = MessageBox.Show("この契約を下書きに戻します（「定期請求の実行」の対象から外れます。生成済みの請求書・仕訳には影響しません）。よろしいですか？", "下書きに戻す", "キャンセル");
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
