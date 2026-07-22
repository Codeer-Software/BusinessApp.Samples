// RecurringBilling.mod.cs — 定期請求契約
// 部門: 作成者の所属部門を初期値にする（スナップショット思想。ddl/330・経費申請と同じ）
void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        if (DepartmentRef.Value == null) { DepartmentRef.Value = CurrentUser.所属部門.Value; }
    }
}
