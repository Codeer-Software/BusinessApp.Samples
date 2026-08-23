namespace BusinessApp.AccountingCore.Accounts;

/// <summary>
/// メモリ上の科目一覧による <see cref="IAccountLookup"/>。
/// 読み込み済みマスタの保持と、テストでの差し替えに使う。
/// </summary>
public sealed class AccountCatalog : IAccountLookup
{
    private readonly IReadOnlyDictionary<AccountId, AccountDefinition> _byId;

    public AccountCatalog(IEnumerable<AccountDefinition> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _byId = accounts.ToDictionary(a => a.Id);
    }

    public AccountDefinition? Find(AccountId accountId)
        => _byId.TryGetValue(accountId, out var account) ? account : null;
}
