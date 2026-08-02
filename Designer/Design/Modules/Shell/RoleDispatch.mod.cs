// RoleDispatch.mod.cs — ルートフレーム（Main）のトップページ 兼 各フレームの「マイページ」リンク先
// CLB は「IsApplicationRoot のフレームは全員アクセス可能」が前提のため、
// ルートには中身を置かず、ログインユーザーの権限に応じたホームフレームへ即時転送する（ADR-0028 → 部品アーキテクチャ再編で刷新）。
// 権限はロールではなく AppUser のキャッシュ列（部門メンバーシップ＋管理者フラグから DB トリガーが導出）で判定する。
void Detail_OnAfterInit()
{
    var frame = "ExpenseStaff";
    if (CurrentUser.IsSysAdmin.Value == true) { frame = "MasterAdmin"; }
    else if (CurrentUser.HasAccountingAccess.Value == true) { frame = "Accounting"; }
    else if (CurrentUser.IsApprover.Value == true) { frame = "ExpenseApprover"; }
    else if (CurrentUser.HasSalesAccess.Value == true) { frame = "SalesStaff"; }
    NavigationService.NavigateTo($"/{frame}/Top");
}
