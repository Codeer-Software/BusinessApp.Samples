namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上が検証で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// 違反を<b>全件</b>持って投げる。1 件だけ見せると、利用者は直しては弾かれを繰り返す。
/// </remarks>
public sealed class JournalPostingRejectedException(IReadOnlyList<Violation> violations)
    : Exception(BuildMessage(violations))
{
    public IReadOnlyList<Violation> Violations { get; } = violations;

    private static string BuildMessage(IReadOnlyList<Violation> violations)
    {
        var errorMessages = violations
            .Where(v => v.Severity == ViolationSeverity.Error)
            .Select(v => $"・{Describe(v)}");
        return $"""
            計上できません。
            {string.Join("\n", errorMessages)}
            """;
    }

    private static string Describe(Violation violation)
        => violation.LineNo is int lineNo ? $"{lineNo} 行目: {violation.Message}" : violation.Message;
}
