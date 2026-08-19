// SubAccount.mod.cs — 補助科目。新規作成時の既定値だけを持つ
//
// CLB の Boolean は**新規時が常に未チェック**で、明示的に 0 が送られるため
// DDL の `DEFAULT 1` は効かない（ADR-0054 の既知の静かな失敗）。
// 入れ忘れると「登録したのに一覧・ピッカーに出てこない」になる。
// 同型は Account / Partner / Project / Department など 9 モジュールに先例がある

void Detail_OnAfterInit()
{
    if (this.IsNewData) { IsActive.Value = true; }
}
