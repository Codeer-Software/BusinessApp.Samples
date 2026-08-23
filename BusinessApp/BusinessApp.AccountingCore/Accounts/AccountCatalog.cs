namespace BusinessApp.AccountingCore.Accounts;

/// <summary>
/// メモリ上の科目一覧による <see cref="IAccountLookup"/>。
/// 読み込み済みマスタの保持と、テストでの差し替えに使う。
/// </summary>
public sealed class AccountCatalog : IAccountLookup
{
    private readonly IReadOnlyDictionary<string, AccountDefinition> _byId;

    public AccountCatalog(IEnumerable<AccountDefinition> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _byId = accounts.ToDictionary(a => a.Id, StringComparer.Ordinal);
    }

    public AccountDefinition? Find(string accountId)
        => accountId is not null && _byId.TryGetValue(accountId, out var account) ? account : null;
}
