// ApprovalInbox.mod.cs — 承認待ち（自分が承認する番の申請一覧）
// ADR-0016: approval_inbox_view（approval_flow_member × Active Order × Pending Flow の SQLite VIEW）を読む。
// 1行 = 「Active な Order で自分が Waiting のメンバー」なので、複数課長の並列承認でも全員の受信箱に出る。
// 行の絞り込みは DataReadCondition（current_approver 列 = 自分。ビューでは approver_user_id を同名で公開）で宣言的に行う。
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
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl(ToApproverModule(parentModule), parentId));
}

// approval_flow.parent_module_name は「申請者が申請したときのモジュール名」が保存された値で、
// 常に "ExpenseRequest"（ApprovalFlow.mod.cs:812）。閲覧者ごとに行き先を変える写像は DB では作れず、
// ここでスクリプトとして書くしかない（ADR-0069 帰結）。
// 承認者を申請者用モジュールへ送ると、明細（ExpenseRequestLine）の行条件 Creator == CurrentUser に
// 引っかかって **エラーにならず明細が空**になる。承認者用モジュールへ写像してこれを塞ぐ。
string ToApproverModule(string parentModuleName)
{
    if (parentModuleName == "ExpenseRequest") return "ExpenseRequestApproval";
    return parentModuleName;
}
