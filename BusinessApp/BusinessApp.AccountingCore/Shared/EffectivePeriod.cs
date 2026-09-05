namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 有効期間（両端を含む閉区間。終期なしを許す）。
/// 税率・控除割合・耐用年数などの制度値は、この期間つきのルールデータとして持つ（docs/15_実装の原則.md §2）。
/// </summary>
public readonly record struct EffectivePeriod
{
    public EffectivePeriod(DateOnly from, DateOnly? to)
    {
        if (to is DateOnly end && end < from)
        {
            throw new ArgumentException($"終期が始期より前になっている: {from:yyyy/MM/dd} 〜 {end:yyyy/MM/dd}", nameof(to));
        }
        From = from;
        To = to;
    }

    /// <summary>始期（この日を含む）。</summary>
    public DateOnly From { get; }

    /// <summary>終期（この日を含む）。null は終期なし。</summary>
    public DateOnly? To { get; }

    public bool Includes(DateOnly date) => date >= From && (To is not DateOnly end || date <= end);

    public bool Overlaps(EffectivePeriod other)
        => (To is not DateOnly end || end >= other.From) && (other.To is not DateOnly otherEnd || otherEnd >= From);

    public override string ToString() => $"{From:yyyy/MM/dd}〜{(To is DateOnly to ? $"{to:yyyy/MM/dd}" : string.Empty)}";
}
