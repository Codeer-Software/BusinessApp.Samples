// BankAccount.mod.cs — BankAccount マスタ

// マスタの有効フラグは DB 既定が 1 だが、**CLB の Boolean は新規作成で常に未チェック**になる
// （CLB-017・実測）。既定が効いていると思って保存すると、作った直後から無効なマスタができ、
// 参照側のピッカーに出てこない。新規のときだけ明示的に立てる
void Detail_OnAfterInit()
{
    if (IsNewData && IsActive.Value != true) { IsActive.Value = true; }
}
