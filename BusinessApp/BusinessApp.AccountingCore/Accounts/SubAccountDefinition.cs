namespace BusinessApp.AccountingCore.Accounts;

/// <summary>補助科目（docs/04 §6）。必ず 1 つの勘定科目に属する。</summary>
/// <param name="Id">補助科目の識別子。</param>
/// <param name="AccountId">属する勘定科目。</param>
/// <param name="Code">補助科目コード（勘定科目の中で一意）。</param>
/// <param name="Name">補助科目名。</param>
/// <param name="IsActive">入力候補に出すか。</param>
public sealed record SubAccountDefinition(
    SubAccountId Id,
    AccountId AccountId,
    string Code,
    string Name,
    bool IsActive = true);
