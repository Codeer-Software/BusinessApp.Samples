namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 検証で見つかった違反。<see cref="Code"/> の値はモジュールごとに定義する
/// （仕訳なら Journals の JournalViolationCodes）。共有カーネルは値を知らない。
/// </summary>
/// <param name="Code">違反の識別子。画面はこれで分岐し、文言は表示にだけ使う。</param>
/// <param name="Message">利用者に見せる説明。</param>
/// <param name="LineNo">対象の明細行。伝票全体の違反は null。</param>
/// <param name="Severity">
/// 既定は <see cref="ViolationSeverity.Error"/>。
/// <b>「戻り値が空なら OK」と書かない</b>こと。警告は返るが計上はできる。
/// </param>
public sealed record Violation(
    string Code,
    string Message,
    int? LineNo = null,
    ViolationSeverity Severity = ViolationSeverity.Error)
{
    public override string ToString()
        => LineNo is int no ? $"[{Code}] {no} 行目: {Message}" : $"[{Code}] {Message}";
}

public static class ViolationEnumerableExtensions
{
    /// <summary>計上を止める違反があるか。警告だけなら false。</summary>
    public static bool HasError(this IEnumerable<Violation> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);
        return violations.Any(v => v.Severity == ViolationSeverity.Error);
    }
}
