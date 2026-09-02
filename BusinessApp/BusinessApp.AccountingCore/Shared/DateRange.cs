namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 閉じた日付の範囲（両端を含む）。会計年度・会計期間のように<b>必ず終わりがある</b>期間に使う。
/// </summary>
/// <remarks>
/// 終期を許容する <see cref="EffectivePeriod"/> と分けてあるのは、
/// 「終わらない会計期間」を型として作れないようにするためである。
/// 終わらない期間は <c>ResolvePeriod</c> がその日以降すべてを拾う、という壊れ方をする。
/// DB 側も <c>end_date NOT NULL</c> なので、C# の型が DB より緩いままにしない（ADR-0014 の考え方）。
/// </remarks>
public readonly record struct DateRange
{
    public DateRange(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentException($"終わりが始まりより前になっている: {from:yyyy/MM/dd} 〜 {to:yyyy/MM/dd}", nameof(to));
        }
        From = from;
        To = to;
    }

    /// <summary>始まりの日（この日を含む）。</summary>
    public DateOnly From { get; }

    /// <summary>終わりの日（この日を含む）。</summary>
    public DateOnly To { get; }

    public bool Includes(DateOnly date) => date >= From && date <= To;

    public bool Overlaps(DateRange other) => From <= other.To && other.From <= To;

    public override string ToString() => $"{From:yyyy/MM/dd}〜{To:yyyy/MM/dd}";
}
