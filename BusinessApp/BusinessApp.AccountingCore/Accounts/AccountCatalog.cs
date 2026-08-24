namespace BusinessApp.AccountingCore.Accounts;

/// <summary>
/// 読み込み済みの勘定科目。検証・集計はここから引く。
/// </summary>
/// <remarks>
/// <b>インターフェースを被せていない。</b> 会計マスタは全件を先に読める規模（科目は 100 件程度）で、
/// 差し替える実装も無い。メモリ上の値の集まりに <c>IXxx</c> を被せるのは、
/// テストを楽にせず読みにくくするだけである（ADR-0012 §6）。
/// 「必要なデータは呼び出し側が読んで渡す」という形（ADR-0013 §5）は具象のままで成り立つ。
/// </remarks>
public sealed class AccountCatalog
{
    private readonly IReadOnlyDictionary<AccountId, AccountDefinition> _byId;

    public AccountCatalog(IEnumerable<AccountDefinition> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        _byId = accounts.ToDictionary(a => a.Id);
    }

    /// <summary>識別子で引く。見つからなければ null。</summary>
    public AccountDefinition? Find(AccountId accountId)
        => _byId.TryGetValue(accountId, out var account) ? account : null;
}
