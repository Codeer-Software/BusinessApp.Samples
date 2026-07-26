// ApprovalRouteRule.mod.cs — 承認ルート判定ルール（ADR-0023）
// 新規作成時は「有効」を既定 ON にする（初見UXテスト U5-5 と同型の罠の予防）
void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        IsActive.Value = true;
    }
}
