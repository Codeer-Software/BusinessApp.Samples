namespace BusinessApp.AccountingCore.Primitives;

/// <summary>端数処理の方法（docs/06 §4）。用途ごとに認められる方法が違うため、既定値を置かない。</summary>
public enum RoundingMode
{
    /// <summary>切捨て。</summary>
    Truncate,

    /// <summary>四捨五入。</summary>
    Round,

    /// <summary>切上げ。</summary>
    Ceiling,
}

public static class Rounding
{
    /// <summary>円未満を指定の方法で処理する。負の値は絶対値に対して同じ向きに処理する。</summary>
    public static decimal Apply(decimal value, RoundingMode mode) => mode switch
    {
        RoundingMode.Truncate => decimal.Truncate(value),
        RoundingMode.Round => decimal.Round(value, 0, MidpointRounding.AwayFromZero),
        RoundingMode.Ceiling => value < 0m ? decimal.Floor(value) : decimal.Ceiling(value),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知の端数処理"),
    };
}
