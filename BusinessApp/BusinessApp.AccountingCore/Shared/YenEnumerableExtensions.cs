namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 金額の列に対する拡張。<see cref="Yen"/> そのものの拡張ではないので、別のファイルに置く。
/// </summary>
public static class YenEnumerableExtensions
{
    public static Yen Sum(this IEnumerable<Yen> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var total = Yen.Zero;
        foreach (var value in source)
        {
            total += value;
        }
        return total;
    }
}
