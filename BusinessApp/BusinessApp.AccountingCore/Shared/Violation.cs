namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 検証で見つかった違反。<see cref="Code"/> の値はモジュールごとに定義する
/// （仕訳なら Journals の JournalViolationCodes）。共有カーネルは値を知らない。
/// </summary>
/// <param name="Code">違反の識別子。</param>
/// <param name="Message">利用者に見せる説明。</param>
/// <param name="LineNo">対象の明細行。伝票全体の違反は null。</param>
public sealed record Violation(string Code, string Message, int? LineNo = null)
{
    public override string ToString() => LineNo is { } no ? $"[{Code}] {no} 行目: {Message}" : $"[{Code}] {Message}";
}
