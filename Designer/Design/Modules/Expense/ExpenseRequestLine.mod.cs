// 明細 1 件分の入力フォーム（ExpenseRequest に ModuleField で埋め込まれる "entry" レイアウト）の挙動。
// このモジュールが AI 読み取りと領収書の添付を**自分で**持つのが要点。
// ヘッダ側で読み取ってから行へ写す形にすると、CLB のスクリプトからは FileField の実体
// （FileGuid / FileSize）に触れないため領収書だけ移せない（docs/12 の FB-048）。
// 最初からこの行のものとして受け取れば、コピーが要らない。

// AI 読み取り完了時: 費目から税区分を補い、少額基準を超える資産性支出なら固定資産にする。
// AI が触ってはいけない項目（税区分・案件・固定資産・行番号）は Remarks で禁じているが、
// 返してきた場合に備えてここで整える。
void AiLine_Completed()
{
    var cat = FindCategory(ExpenseCategoryRef.Value);
    ApplyCategoryDefaults(cat);
    Toaster.Info("レシートを読み取りました。内容を確認して「この内容で追加」を押してください");
}

// 費目を選んだとき: 税区分の既定を入れ、不課税なら手入力の消費税を捨て、少額資産を判定する
void Category_OnDataChanged()
{
    ApplyCategoryDefaults(FindCategory(ExpenseCategoryRef.Value));
}

// 金額を変えたとき: 少額資産の判定をやり直す
void Amount_OnDataChanged()
{
    ApplyAssetSuggestion(FindCategory(ExpenseCategoryRef.Value));
}

// 利用日を変えたとき: 少額基準は日付で期間解決するので判定をやり直す
void UsedDate_OnDataChanged()
{
    ApplyAssetSuggestion(FindCategory(ExpenseCategoryRef.Value));
}

// 税区分を変えたとき: 消費税の対象外にしたなら手入力の「うち消費税」を捨てる
void TaxCategory_OnDataChanged()
{
    if (IsTaxablePurchase()) return;
    if (TaxAmount.Value == null || TaxAmount.Value <= 0) return;
    var cleared = TaxAmount.Value;
    TaxAmount.Value = null;
    Toaster.Info($"消費税の対象外の税区分のため、「うち消費税」{cleared:#,0} 円をクリアしました");
}

// 費目にひもづく既定の反映（税区分・不課税時のクリア・少額資産）
void ApplyCategoryDefaults(ExpenseCategory cat)
{
    if (cat == null) return;

    if (TaxCategoryRef.Value == null)
    {
        TaxCategoryRef.Value = cat.DefaultTaxCategory.Value;
    }

    // 不課税・非課税の費目では手入力の消費税が仕訳に効かないので捨てる（ADR-0051）
    if (!IsTaxablePurchase() && TaxAmount.Value != null && TaxAmount.Value > 0)
    {
        var cleared = TaxAmount.Value;
        TaxAmount.Value = null;
        Toaster.Info($"「{cat.Name.Value}」は消費税の対象外の費目のため、「うち消費税」{cleared:#,0} 円をクリアしました");
    }

    ApplyAssetSuggestion(cat);
}

// 資産性の費目 × 少額基準以上なら固定資産計上対象を自動 ON（利用者は手で外せる）
void ApplyAssetSuggestion(ExpenseCategory cat)
{
    var isCandidate = (cat != null) && (cat.IsAssetCandidate.Value == true);
    if (!isCandidate)
    {
        if (IsFixedAsset.Value == true) IsFixedAsset.Value = false;
        return;
    }
    var limit = GetThresholdAmountAt("SMALL_ASSET_EXPENSE", UsedDate.Value);
    var amt = Amount.Value ?? 0;
    if (limit > 0 && amt >= limit && IsFixedAsset.Value != true)
    {
        IsFixedAsset.Value = true;
        Toaster.Info($"金額 {amt:#,0} 円 ≧ 少額基準 {limit:#,0} 円のため固定資産計上対象にしました（承認後に固定資産台帳へ登録されます）");
    }
}

// この行の税区分が課税仕入か
bool IsTaxablePurchase()
{
    if (TaxCategoryRef.Value == null) return false;
    var s = new ModuleSearcher<TaxCategory>();
    s.AddEquals(c => c.Id.Value, TaxCategoryRef.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return false;
    return (((TaxCategory)found).TaxationType.Value == "taxable_purchase");
}

ExpenseCategory FindCategory(object categoryId)
{
    if (categoryId == null) return null;
    var s = new ModuleSearcher<ExpenseCategory>();
    s.AddEquals(c => c.Id.Value, categoryId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (ExpenseCategory)found;
}

// system_thresholds から指定コードの閾値を、指定日で期間解決して取得（該当なしは 0）
int GetThresholdAmountAt(string code, var d)
{
    var s = new ModuleSearcher<SystemThreshold>();
    var thresholds = s.Execute();
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
