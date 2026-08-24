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
}
