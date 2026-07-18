// RoleDispatch.mod.cs — ルートフレーム（Main）のトップページ
// CLB は「IsApplicationRoot のフレームは全員アクセス可能」が前提のため、
// ルートには中身を置かず、ログインユーザーのロールに応じたロール別フレームへ即時転送する（ADR-0028）
void Detail_OnAfterInit()
{
    var role = CurrentUser.Role.Value;
    var frame = "General";
    if (role == "approver") { frame = "Approver"; }
    if (role == "accounting") { frame = "Accounting"; }
    if (role == "sysadmin") { frame = "Sysadmin"; }
    NavigationService.NavigateTo($"/{frame}/Home");
}
