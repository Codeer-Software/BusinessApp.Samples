namespace BusinessApp.AccountingCore.Shared;

/// <summary>違反の重み。</summary>
public enum ViolationSeverity
{
    /// <summary>これがあると計上できない。</summary>
    Error,

    /// <summary>
    /// 計上は止めないが知らせる。優良な電子帳簿の適用開始日が年度の開始日と一致しない、
    /// といった「間違いとは言い切れないが確認してほしい」ものに使う。
    /// </summary>
    Warning,
}
