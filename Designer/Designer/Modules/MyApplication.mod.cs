// MyApplication.mod.cs — 申請中（自分が申請したものの一覧・approval_flow の読み取り専用ビュー）
// 行の絞り込みは DataReadCondition（申請者=自分）で宣言的に行う。全ロールが利用できる。
// 「開く」は申請モジュール（現状 ExpenseRequest）の詳細へ遷移する。

void OpenRequest_OnClick()
{
    // ListField 経由の行はフィールドの .Value が遅延ロードで空のことがあるため DB から取り直す
    var s = new ModuleSearcher<MyApplication>();
    s.AddEquals(f => f.Id.Value, Id.Value);
    var rs = s.Execute();
    if (rs.Count == 0) return;
    var parentModule = rs[0].ParentModuleName.Value;
    var parentId = rs[0].ParentId.Value;
    if (string.IsNullOrEmpty(parentModule) || string.IsNullOrEmpty(parentId)) return;
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl(parentModule, parentId));
}
