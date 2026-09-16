// 検索欄に入れた前後の空白を落とす（規則は docs/21 §0、書き方は Designer/Project.md）。
// **`ShouldTrimAfterEdit` は検索欄では落とさない**——理由と実測は qa/01 の E-09。
void Keyword_OnSearchDataChanged()
{
    var trimmed = Keyword.SearchValue?.Trim();
    // 同じ字なら書き戻さない——書き戻しがこの手をもう一度呼ぶ形にしない。
    if (trimmed == Keyword.SearchValue) return;

    Keyword.SearchValue = trimmed;
}
