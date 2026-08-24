namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上が検証で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// 違反を<b>全件</b>持って投げる。1 件だけ見せると、利用者は直しては弾かれを繰り返す。
/// </remarks>
public sealed class JournalPostingRejectedException : Exception
{
    public JournalPostingRejectedException(IReadOnlyList<Violation> violations)
        : base(BuildMessage(violations))
        => Violations = violations;

    public IReadOnlyList<Violation> Violations { get; }

    private static string BuildMessage(IReadOnlyList<Violation> violations)
    {
        var errors = violations.Where(v => v.Severity == ViolationSeverity.Error).ToList();
        return "計上できません。" + Environment.NewLine
            + string.Join(Environment.NewLine, errors.Select(v => "・" + Describe(v)));
    }

    private static string Describe(Violation violation)
        => violation.LineNo is { } lineNo ? $"{lineNo} 行目: {violation.Message}" : violation.Message;
}
