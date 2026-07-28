// 月次推移表: 検索初期値（サイドバーから開いたとき PL を既定選択にする。
// 未選択でも SQL 側の COALESCE で PL 扱いになるため、これは表示の明示のみ）
void Search_OnInitialization()
{
    StatementSel.SearchValue = "PL";
}
