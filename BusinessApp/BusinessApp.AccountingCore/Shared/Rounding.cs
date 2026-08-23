namespace BusinessApp.AccountingCore.Shared;

/// <summary>円未満の端数処理。</summary>
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
