// MatchingRule.mod.cs — 仕訳ルール
// 新規作成時は「有効」を既定 ON にする（初見UXテスト U5-5: 初期 OFF だと無効ルールを作る罠）
void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        IsActive.Value = true;
    }
}
