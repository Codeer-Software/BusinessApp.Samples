// Department.mod.cs — 部課階層のガード（ADR-0044）
// departments は自己参照 2 階層（親部門なし=部 / あり=課）。node_type は DB トリガーが保守する。
// 親部門の候補は部ノードのみに絞ってあるが、画面から作れる不正（自己参照・3階層化）をここで防ぐ。

void ParentRef_OnDataChanged()
{
    if (ParentRef.Value == null) return;

    if ($"{ParentRef.Value}" == $"{Id.Value}")
    {
        ParentRef.Value = null;
        Toaster.Error("自分自身を親部門にはできません");
        return;
    }

    // 2階層まで: 課（子ノード）を持つ部門を課にはできない
    if (!IsNewData)
    {
        var s = new ModuleSearcher<Department>();
        s.AddEquals(d => d.ParentRef.Value, Id.Value);
        if (s.Execute().Count > 0)
        {
            ParentRef.Value = null;
            Toaster.Error("課を持つ部門を課にはできません（階層は部・課の2階層まで）");
        }
    }
}

// マスタの有効フラグは DB 既定が 1 だが、**CLB の Boolean は新規作成で常に未チェック**になる
// （CLB-017・実測）。既定が効いていると思って保存すると、作った直後から無効なマスタができ、
// 参照側のピッカーに出てこない。新規のときだけ明示的に立てる
void Detail_OnAfterInit()
{
    if (IsNewData && IsActive.Value != true) { IsActive.Value = true; }
}
