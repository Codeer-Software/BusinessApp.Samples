namespace BusinessApp.AccountingCore.Accounts;

/// <summary>読み込み済みの補助科目。</summary>
public sealed class SubAccountCatalog
{
    private readonly IReadOnlyDictionary<SubAccountId, SubAccountDefinition> _byId;

    public SubAccountCatalog(IEnumerable<SubAccountDefinition> subAccounts)
    {
        ArgumentNullException.ThrowIfNull(subAccounts);
        _byId = subAccounts.ToDictionary(s => s.Id);
    }

    public SubAccountDefinition? Find(SubAccountId subAccountId)
        => _byId.TryGetValue(subAccountId, out var subAccount) ? subAccount : null;

    /// <summary>その勘定科目に、<b>いま選べる</b>補助科目が 1 つでもあるか。</summary>
    /// <remarks>
    /// <b>「補助科目を選んでください」と言えるかを決める。</b> 1 つも無い科目でそう言うと、
    /// 候補ダイアログが 0 件で開くだけで、利用者は次の一手を踏めない
    /// （画面の候補は「有効 かつ その科目のもの」で絞る。docs/21 §1）。
    /// <b>無効な補助科目は数えない</b>——新しい計上には使えないので、あっても選べない。
    /// </remarks>
    public bool HasSelectable(AccountId accountId)
        => _byId.Values.Any(s => s.AccountId == accountId && s.IsActive);
}
