// GeneralLedger.mod.cs — 総勘定元帳
// 検索初期値: 当年度の期首〜期末（初見UXテスト U3-4: 期間既定が無く全期間表示だった）
// 合計残高試算表からのドリルダウン（ADR-0065）で来たときは、URL パラメータの科目・期間を優先する。

// 補助科目カスケード: 検索フォームの入力は SearchValue に入るため、Value へコピーして
// SubAccountSel の候補絞り込み（AccountId=Account.Value）をリアルタイムに効かせる（CascadeInputBySearch パターン）
void Account_OnSearchDataChanged()
{
    Account.Value = Account.SearchValue;
}

void Search_OnInitialization()
{
    // ① 試算表からのドリルダウン（?drill_account=... ）を先に適用する
    var applied = ApplyDrillDownParameters();

    // ② 期間の既定（当年度の期首〜期末）。ドリルダウンで期間が来ていれば上書きしない
    var fy = new FiscalYear().ResolveDisplayYear();
    if (fy == null) return;
    var typed = (FiscalYear)fy;
    if (DateFrom.SearchMin == null) { DateFrom.SearchMin = typed.StartDate.Value; }
    if (DateTo.SearchMin == null) { DateTo.SearchMin = typed.EndDate.Value; }
}

// 試算表の「元帳」ボタンが付ける URL パラメータを検索条件へ移す。
// 科目は SearchValue（元帳の必須条件）、期間は SearchMin（範囲検索の下限側に既定を置く流儀）。
// Account.Value にも入れるのは、補助科目のカスケード候補を初回表示から効かせるため。
bool ApplyDrillDownParameters()
{
    var qs = NavigationService.GetUniqueQueryParameters();
    if (qs == null) { return false; }

    var applied = false;
    if (qs.ContainsKey("drill_account"))
    {
        var raw = qs["drill_account"];
        if (!string.IsNullOrEmpty(raw))
        {
            Account.SearchValue = raw;
            Account.Value = raw;
            applied = true;
        }
    }
    if (qs.ContainsKey("drill_from"))
    {
        var d = ParseDate(qs["drill_from"]);
        if (d != null) { DateFrom.SearchMin = d; }
    }
    if (qs.ContainsKey("drill_to"))
    {
        var d = ParseDate(qs["drill_to"]);
        if (d != null) { DateTo.SearchMin = d; }
    }
    return applied;
}

// "yyyy-MM-dd" を DateTime へ。壊れた値は無視して既定にフォールバックする
DateTime? ParseDate(string text)
{
    if (string.IsNullOrEmpty(text)) { return null; }
    var parts = text.Split('-');
    if (parts.Length != 3) { return null; }
    var y = 0;
    var m = 0;
    var d = 0;
    if (!int.TryParse(parts[0], out y)) { return null; }
    if (!int.TryParse(parts[1], out m)) { return null; }
    if (!int.TryParse(parts[2], out d)) { return null; }
    if (y < 1900 || m < 1 || m > 12 || d < 1 || d > 31) { return null; }
    return new DateTime(y, m, d);
}
