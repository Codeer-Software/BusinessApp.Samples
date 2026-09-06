namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 端数処理の方法（docs/11 §4）。
/// <b>既定値を置かない。</b> 同じ「端数処理」でも場面ごとに認められる方法が違い、
/// 既定に流されると法令上許されない方法が黙って使われる。
/// </summary>
public enum RoundingMode
{
    /// <summary>切捨て。</summary>
    Truncate,

    /// <summary>四捨五入。</summary>
    Round,

    /// <summary>切上げ。</summary>
    Ceiling,
}
