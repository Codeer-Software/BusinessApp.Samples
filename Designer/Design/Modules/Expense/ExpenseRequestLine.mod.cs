// 明細 1 件分の入力フォーム（ExpenseRequest に ModuleField で埋め込まれる "entry" レイアウト）の挙動。
// このモジュールが AI 読み取りと領収書の添付を**自分で**持つのが要点。
// ヘッダ側で読み取ってから行へ写す形にすると、CLB のスクリプトからは FileField の実体
// （FileGuid / FileSize）に触れないため領収書だけ移せない（docs/12 の FB-048）。
// 最初からこの行のものとして受け取れば、コピーが要らない。

// AI 読み取り完了時: 費目から税区分を補い、少額基準を超える資産性支出なら固定資産にする。
// AI が触ってはいけない項目（税区分・案件・固定資産・行番号）は Remarks で禁じているが、
// 返してきた場合に備えてここで整える。
// 明細フォームを開いたとき（新規行・既存行の読込のどちらも通る）。
// 「うち消費税」欄の出し分けを初期表示から効かせる（BUG-0192）
void Entry_OnAfterInitialization()
{
    UpdateTaxAmountVisibility();
}

// 「うち消費税」欄は**課税仕入の行にだけ出す**（BUG-0192）。
//
// docs/07 §7 と ADR-0066 の B-03 は「不課税・非課税の費目では欄を隠す」と規定しているのに、
// 明細モジュールには可視制御が 1 行も無かった。欄が出たままだと不課税の行にも数字を入れられ、
// `ValidateLineTax` は課税仕入でないため素通しするので、**`expense_request_lines.tax_amount` に
// 残った値がヘッダ合計と食い違う**（仕訳には効かないので帳簿は合うが、画面の数字だけが合わない）。
//
// 隠すときに値を捨てるのは `ApplyCategoryDefaults` / `TaxCategory_OnDataChanged` が既にやっている
// （そちらは理由を Toaster で言う）。ここは**見え方だけ**を受け持つ
void UpdateTaxAmountVisibility()
{
    var taxable = IsTaxablePurchase();
    LblTax.IsVisible = taxable;
    TaxAmount.IsVisible = taxable;
}

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

// 税区分を変えたとき: 消費税の対象外にしたなら手入力の「うち消費税」を捨てる。
// 税抜金額が変わるので少額資産の判定もやり直す（BUG-0184）
void TaxCategory_OnDataChanged()
{
    // 人が選び直したら自動セットの痕跡を捨てる。以後この行では費目を変えても税区分を上書きしない
    // （`ApplyCategoryDefaults` が自分で入れた直後もここを通るので、値が一致するときだけ痕跡を残す）
    if (TaxCategoryAutoValue.Value != $"{TaxCategoryRef.Value}") { TaxCategoryAutoValue.Value = ""; }

    if (!IsTaxablePurchase() && TaxAmount.Value != null && TaxAmount.Value > 0)
    {
        var cleared = TaxAmount.Value;
        TaxAmount.Value = null;
        Toaster.Info($"消費税の対象外の税区分のため、「うち消費税」{cleared:#,0} 円をクリアしました");
    }
    UpdateTaxAmountVisibility();
    ApplyAssetSuggestion(FindCategory(ExpenseCategoryRef.Value));
}

// 「うち消費税」を変えたとき: 税抜金額が動くので少額資産の判定をやり直す（BUG-0184）
void TaxAmount_OnDataChanged()
{
    ApplyAssetSuggestion(FindCategory(ExpenseCategoryRef.Value));
}

// 費目にひもづく既定の反映（税区分・不課税時のクリア・少額資産）
void ApplyCategoryDefaults(ExpenseCategory cat)
{
    if (cat == null) return;

    // **税区分は費目に追従する。人が手で選んだ税区分は、その行の費目が変わるまで保持する**（BUG-0182）。
    // 旧実装は「税区分が空のときだけ既定を入れる」だったので、
    // 費目「消耗品費」（課税仕入 10%）→ 費目「諸会費」（不課税）に直しても税区分が残り、
    // **不課税の経費に仮払消費税が立った**（逆向きなら控除もれ）。
    // 仕訳コアの BUG-0067 と同じ形なので、同じ直し方（自動セットの痕跡を控える）を採る。
    // 控えは非 DB 項目（`DataOnlyFields` に登録済み——登録し忘れると CLB がロードせず修正が丸ごと無効になる）
    var catKey = $"{cat.Id.Value}";
    var autoFrom = TaxCategoryAutoFrom.Value ?? "";
    var autoValue = TaxCategoryAutoValue.Value ?? "";
    var current = $"{TaxCategoryRef.Value}";
    var isUntouched = (TaxCategoryRef.Value == null) || (autoValue != "" && autoValue == current);

    if (isUntouched && autoFrom != catKey)
    {
        TaxCategoryRef.Value = cat.DefaultTaxCategory.Value;
        TaxCategoryAutoValue.Value = $"{cat.DefaultTaxCategory.Value}";
    }
    TaxCategoryAutoFrom.Value = catKey;

    // 不課税・非課税の費目では手入力の消費税が仕訳に効かないので捨てる（ADR-0051）
    if (!IsTaxablePurchase() && TaxAmount.Value != null && TaxAmount.Value > 0)
    {
        var cleared = TaxAmount.Value;
        TaxAmount.Value = null;
        Toaster.Info($"「{cat.Name.Value}」は消費税の対象外の費目のため、「うち消費税」{cleared:#,0} 円をクリアしました");
    }

    UpdateTaxAmountVisibility();
    ApplyAssetSuggestion(cat);
}

// 資産性の費目 × 少額基準以上なら固定資産計上対象を自動 ON（利用者は手で外せる）
void ApplyAssetSuggestion(ExpenseCategory cat)
{
    // **人が手で動かしたチェックは、以後この行では自動で触らない**（BUG-0189・ADR-0066 D-05）。
    //
    // 旧実装は「今 OFF なら ON にする」だけだったので、100,000 円で自動 ON → 意図して OFF →
    // 打ち間違いを 100,500 円に直す、で**また ON に戻った**。何度外しても戻ってくる。
    // 自動で入れた値を痕跡（`IsFixedAssetAutoValue`）に控え、**現在値が痕跡と一致している間だけ**
    // 追随させる（税区分 BUG-0182・請求書 BUG-0182・見積 BUG-0423 と同じ型）
    if ((IsFixedAssetAutoValue.Value ?? "") == "manual") return;

    var isCandidate = (cat != null) && (cat.IsAssetCandidate.Value == true);
    if (!isCandidate)
    {
        if (IsFixedAsset.Value == true)
        {
            IsFixedAsset.Value = false;
            IsFixedAssetAutoValue.Value = "false";
        }
        return;
    }
    var limit = GetThresholdAmountAt("SMALL_ASSET_EXPENSE", UsedDate.Value);
    // **判定は税抜で行う**（BUG-0184）。このアプリは税抜経理（仮払消費税を分離）なので、
    // 少額減価償却資産の 10 万円判定も税抜が正しい（消費税法基本通達 11-4-1 の考え方）。
    // 税込で判定していた頃は、税込 105,000 円（税抜 95,455 円）の備品が自動 ON になったうえで
    // **台帳には税抜 95,455 円で登録される**——判定と登録の基準が食い違い、
    // 本来その期に全額損金にできる 1 台が 4 年に分かれて償却されていた。
    // 台帳登録側（`ExpenseRequestAccounting`）は最初から税抜なので、こちらを合わせる
    var amt = NetAmountForAssetJudge();
    if (limit > 0 && amt >= limit && IsFixedAsset.Value != true)
    {
        IsFixedAsset.Value = true;
        IsFixedAssetAutoValue.Value = "true";
        Toaster.Info($"税抜金額 {amt:#,0} 円 ≧ 少額基準 {limit:#,0} 円のため固定資産計上対象にしました（承認後に固定資産台帳へ登録されます）");
    }
    // 金額を下げたときも戻す（BUG-0319）。上げるときだけ動いて下げるときに動かないと、
    // 一度でも基準を超えた行が**そのまま台帳に登録される**。
    // 「人が手で付けた ON」と「自動で付いた ON」を区別する列は持たない——
    // 代わりに**外したことを必ず知らせる**ので、意図して付けていたなら気づいて戻せる（静かな失敗にしない）
    else if (limit > 0 && amt < limit && IsFixedAsset.Value == true)
    {
        IsFixedAsset.Value = false;
        IsFixedAssetAutoValue.Value = "false";
        Toaster.Info($"税抜金額 {amt:#,0} 円 < 少額基準 {limit:#,0} 円になったので固定資産の指定を外しました（必要なら手で戻してください）");
    }
}

// 少額資産の判定に使う税抜金額（BUG-0184）。
// レシート記載の税額（手入力）があればそれを優先し、無ければ税区分の税率から内税計算する。
// 課税仕入でない行は税が乗っていないので税込＝税抜。
// **台帳へ登録する取得価額（`ExpenseRequestAccounting` の `gross - CalcLineTax`）と同じ式**にしてある
int NetAmountForAssetJudge()
{
    var gross = Amount.Value ?? 0;
    if (gross <= 0) return 0;
    if (!IsTaxablePurchase()) return gross;

    var manual = TaxAmount.Value ?? 0;
    if (manual > 0 && manual < gross) { return gross - manual; }

    decimal pct = FindTaxRatePercent();
    if (pct == 0) return gross;
    int tax = gross * pct / (100 + pct);
    return gross - tax;
}

// この行の税区分に紐づく税率(%)。未設定・解決不能なら 0
decimal FindTaxRatePercent()
{
    if (TaxCategoryRef.Value == null) return 0;
    var s = new ModuleSearcher<TaxCategory>();
    s.AddEquals(c => c.Id.Value, TaxCategoryRef.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return 0;
    var tcat = (TaxCategory)found;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    return ((TaxRate)foundRate).RatePercent.Value ?? 0;
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

// 固定資産チェックを人が動かしたら、自動セットの痕跡を捨てる（BUG-0189）。
// 以後この行では金額・利用日・費目を直しても自動で ON/OFF しない
void IsFixedAsset_OnDataChanged()
{
    // 自動セットのときもここを通る（値の代入で同期的に発火する）。
    // その場合は呼び元が**この直後に**痕跡を "true"/"false" で上書きするので、"manual" は残らない
    var currentMark = (IsFixedAsset.Value == true) ? "true" : "false";
    if ((IsFixedAssetAutoValue.Value ?? "") != currentMark) { IsFixedAssetAutoValue.Value = "manual"; }
}
