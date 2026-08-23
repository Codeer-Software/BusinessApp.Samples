namespace BusinessApp.AccountingCore.Masters;

/// <summary>
/// 勘定科目の参照。<see cref="AccountingCore"/> は DB を知らないので、
/// 検証に必要なマスタは呼び出し側がこの形で渡す（ADR-0008）。
/// </summary>
public interface IAccountLookup
{
    AccountDefinition? Find(string accountId);
}

/// <summary>メモリ上の科目一覧による <see cref="IAccountLookup"/>。テストと、読み込み済みマスタの保持に使う。</summary>
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
