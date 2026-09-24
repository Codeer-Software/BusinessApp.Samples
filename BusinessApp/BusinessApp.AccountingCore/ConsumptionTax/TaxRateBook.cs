namespace BusinessApp.AccountingCore.ConsumptionTax;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 税率の制度ルール一式（docs/11 §1-1）。<b>税率区分ごとに期間の集合を持つ。</b>
/// </summary>
/// <remarks>
/// <para><b><see cref="EffectiveDatedRuleSet{TRule}"/> を区分ごとに 1 つ作る。</b>
/// 区分をまたいで 1 つの集合にすると、<b>区分が違うだけで期間が重なる行が「重複」として弾かれる</b>
/// ——標準と軽減は同じ日から同時に有効である。</para>
/// <para><b>重なりの検査は区分の中では効いている。</b> DB のトリガも同じ単位で見ているので、
/// <b>同じことを 2 か所が独立に見ている</b>（<c>Designer/ddl/015_tax_rates.sql</c>）。</para>
/// </remarks>
public sealed class TaxRateBook
{
    private readonly IReadOnlyDictionary<TaxRateKind, EffectiveDatedRuleSet<TaxRate>> byKind;

    public TaxRateBook(IEnumerable<TaxRate> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        Rules = rates.OrderBy(rate => rate.Kind).ThenBy(rate => rate.Period.From).ToList();
        byKind = Rules
            .GroupBy(rate => rate.Kind)
            .ToDictionary(group => group.Key, group => new EffectiveDatedRuleSet<TaxRate>(group));
    }

    /// <summary>区分順・有効期間順に並んだ全件。</summary>
    public IReadOnlyList<TaxRate> Rules { get; }

    /// <summary>
    /// その日に、その区分へ当たる税率を返す。<b>当たる行が無ければ null。</b>
    /// </summary>
    /// <remarks>
    /// <b>null は「税率が 0%」ではない。</b> 制度ルールが無い日の取引は計算できないという意味であり、
    /// 呼ぶ側は 0 円を返さずに断る（docs/11 §5-1 の控除割合と同じ扱い）。
    /// </remarks>
    public TaxRate? ResolveAt(TaxRateKind kind, DateOnly date)
        => byKind.TryGetValue(kind, out var rules) ? rules.ResolveAt(date) : null;

    /// <summary>版で引く。<b>過去の仕訳を再現するときは日付ではなく保存された版で引く</b>（I-16）。</summary>
    public TaxRate? ResolveByVersion(RuleVersion version)
        => Rules.FirstOrDefault(rate => rate.Version == version);
}
