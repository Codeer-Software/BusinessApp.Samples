// Account.mod.cs — Account マスタ

// マスタの有効フラグは DB 既定が 1 だが、**CLB の Boolean は新規作成で常に未チェック**になる
// （CLB-017・実測）。既定が効いていると思って保存すると、作った直後から無効なマスタができ、
// 参照側のピッカーに出てこない。新規のときだけ明示的に立てる
void Detail_OnAfterInit()
{
    if (IsNewData && IsActive.Value != true) { IsActive.Value = true; }
}

// 補助科目は詳細画面を持たず、この子リストで作る。**CLB の Boolean は新規行でも未チェック**なので
// （CLB-017）、ここで立てないと「作った直後から無効な補助科目」ができ、
// 仕訳の補助科目ピッカー（有効なものだけを出す）に一生出てこない
void SubAccounts_OnDataChanged()
{
    foreach (var row in SubAccounts.Rows)
    {
        var sa = (SubAccount)row;
        if (sa.Id.Value == null && sa.IsActive.Value != true) { sa.IsActive.Value = true; }
    }
}
