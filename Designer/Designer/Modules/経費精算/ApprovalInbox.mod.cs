// ApprovalInbox.mod.cs — 承認待ち（自分が承認する番の申請一覧・approval_flow の読み取り専用ビュー）
// 行の絞り込みは DataReadCondition（現在の承認者=自分）で宣言的に行う。
// 表示は承認者・経理のみ（UserReadCondition。一般社員はメニューにも出ない）。

void OpenRequest_OnClick()
{
    // ListField 経由の行はフィールドの .Value が遅延ロードで空のことがあるため DB から取り直す
    var s = new ModuleSearcher<ApprovalInbox>();
    s.AddEquals(f => f.Id.Value, Id.Value);
    var rs = s.Execute();
    if (rs.Count == 0) return;
    var parentModule = rs[0].ParentModuleName.Value;
    var parentId = rs[0].ParentId.Value;
    if (string.IsNullOrEmpty(parentModule) || string.IsNullOrEmpty(parentId)) return;
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl(parentModule, parentId));
}
