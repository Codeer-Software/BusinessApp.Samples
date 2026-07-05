// 金額をベースにテンプレートを選択 (ApprovalFlow から呼ばれる)
// B2-3 で費目×金額×例外ルール（3万/20万・交際費=総務必須）に拡張予定
string SelectTemplateName()
{
    var amount = Amount.Value ?? 0;
    return (amount < 50000) ? "SimpleExpense" : "FullExpense";
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
        return;
    }

    // 申請後 (新規でない) は申請内容を変更不可。却下/キャンセル時のみ再申請のため編集可。
    var flowStatus = ApprovalFlow.ChildModule.Status.Value;
    var reopenable = (flowStatus == "Rejected" || flowStatus == "Cancelled");
    EditableGrid.IsEnabled = reopenable;
    UpdateVisibility();
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
    var s = new ModuleSearcher<SystemThreshold>();
    var thresholds = s.Execute();
    var d = ExpenseDate.Value;
    var limit = 0;
    foreach (var t in thresholds)
    {
        var th = (SystemThreshold)t;
        if (th.Code.Value != "SMALL_ASSET_EXPENSE") continue;
        if (d != null && th.ValidFrom.Value != null && d < th.ValidFrom.Value) continue;
        if (d != null && th.ValidTo.Value != null && d > th.ValidTo.Value) continue;
        limit = th.Amount.Value ?? 0;
    }
    return limit;
}
