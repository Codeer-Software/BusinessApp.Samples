// TrialBalance.mod.cs — 合計残高試算表
// 検索初期値: 当年度の期首〜期末（初見UXテスト U3-4/U3-8: 期間未指定だと期首繰越が乗らず
// 預金残高がマイナスに見える誤解を生むため、初期表示を当年度に固定する）
void Search_OnInitialization()
{
    var fy = new FiscalYear().ResolveDisplayYear();
    if (fy == null) return;
    var typed = (FiscalYear)fy;
    if (DateFrom.SearchMin == null) { DateFrom.SearchMin = typed.StartDate.Value; }
    if (DateTo.SearchMin == null) { DateTo.SearchMin = typed.EndDate.Value; }
}

// 科目行 → 総勘定元帳へドリルダウン（ADR-0065）。
// 表示中の期間をそのまま引き継ぐので、試算表の数字と元帳の明細が必ず一致する。
// 受け取り側は GeneralLedger.Search_OnInitialization（?initialize_search=true で発火）。
//
// 【なぜ ButtonField ではなく AnchorTagField か】
// 読み取り専用（CanCreate/CanUpdate/CanDelete=false）のクエリモジュールでは
// **ButtonField の OnClick が発火しない**（ボタンが無効化される。実測 2026-08-16）。
// AnchorTagField の OnClick は発火し、しかも Module/Url が空なら既定のナビゲーションが走らないので、
// スクリプト側の NavigateTo がそのまま効く。クエリ一覧に行アクションを足すときはこの形を使う。
//
// 【合計（貸借検算）行にリンクを出さない方法】
// リスト内のアンカーは `IsVisible = false` では消えない（実測）。SQL 側で `drill_label` を空文字にし、
// アンカーの TitleVariable にバインドしてリンク文字ごと消している。念のため下の null ガードも残す。
void Drill_OnClick()
{
    var accountId = AccountIdRaw.Value;
    if (accountId == null) { return; }

    var url = NavigationService.GetModuleUrl("GeneralLedger")
        + $"?initialize_search=true&drill_account={accountId}";
    if (DateFrom.SearchMin != null) { url = url + $"&drill_from={DateFrom.SearchMin:yyyy-MM-dd}"; }
    if (DateTo.SearchMin != null) { url = url + $"&drill_to={DateTo.SearchMin:yyyy-MM-dd}"; }
    NavigationService.NavigateTo(url);
}
