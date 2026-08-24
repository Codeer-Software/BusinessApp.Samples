// マスタの新規作成時の初期値。
//
// DB の既定は is_active = 1 だが、画面のチェックボックスは既定でオフになる。
// 揃えておかないと、新規作成したマスタが最初から無効になり、入力候補に出ない。
// ADR-0008 のとおり、スクリプトに書いてよいのは画面上の値の出し入れだけである。
void Detail_OnAfterInitialization()
{
    if (!IsNewData) return;

    IsActive.Value = true;
}
