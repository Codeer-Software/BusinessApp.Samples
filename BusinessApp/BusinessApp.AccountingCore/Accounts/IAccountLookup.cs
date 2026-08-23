namespace BusinessApp.AccountingCore.Accounts;

/// <summary>
/// 勘定科目の参照。AccountingCore は DB を知らないので、
/// 検証に必要なマスタは呼び出し側がこの形で渡す（ADR-0008）。
/// </summary>
/// <remarks>
/// 具象ではなくインターフェースを受け取ることで、検証ロジックのテストが
/// DB もマスタ投入も要らずに書ける（ADR-0012）。
/// </remarks>
public interface IAccountLookup
{
    /// <summary>識別子で引く。見つからなければ null。</summary>
    AccountDefinition? Find(AccountId accountId);
}
