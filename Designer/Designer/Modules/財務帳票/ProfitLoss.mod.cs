// ProfitLoss.mod.cs — 損益計算書
// 責務: 一覧行の初期化でカテゴリ境界（区分小計行・段階利益行）にマーカークラスを付ける。
// app.css の [list-module="ProfitLoss"] tr:has(...) が行全体へ太罫線・強調を適用する
// （2026-07-25 ユーザー要望「カテゴリごとに行間の線を太く」。BankStatementLine の行スタイルと同パターン）

void PLRow_OnAfterInit()
{
    if (Section.Value == "段階利益")
    {
        Item.ClassName = "pl-profit-row";
    }
    else if (Item.Value != null && Item.Value.EndsWith(" 計"))
    {
        Item.ClassName = "pl-subtotal-row";
    }
}
