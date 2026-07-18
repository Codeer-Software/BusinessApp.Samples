// GeneralLedger.mod.cs — 総勘定元帳
// 検索初期値: 当年度の期首〜期末（初見UXテスト U3-4: 期間既定が無く全期間表示だった）

// 補助科目カスケード: 検索フォームの入力は SearchValue に入るため、Value へコピーして
// SubAccountSel の候補絞り込み（AccountId=Account.Value）をリアルタイムに効かせる（CascadeInputBySearch パターン）
void Account_OnSearchDataChanged()
{
    Account.Value = Account.SearchValue;
}
void Search_OnInitialization()
{
    var today = DateTime.Today;
    var firstDay = new DateTime(today.Year, today.Month, 1);
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var fy = s.ExecuteFirstOrDefault();
    if (fy == null) return;
    var typed = (FiscalYear)fy;
    if (DateFrom.SearchMin == null) { DateFrom.SearchMin = typed.StartDate.Value; }
    if (DateTo.SearchMin == null) { DateTo.SearchMin = typed.EndDate.Value; }
}
