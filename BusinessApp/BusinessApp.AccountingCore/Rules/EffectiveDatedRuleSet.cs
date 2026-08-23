namespace BusinessApp.AccountingCore.Rules;

using BusinessApp.AccountingCore.Primitives;

/// <summary>有効期間を持つ制度ルール。</summary>
public interface IEffectiveDatedRule
{
    EffectivePeriod Period { get; }

    RuleVersion Version { get; }
}

/// <summary>
/// 有効期間つき制度ルールの集合。税率・控除割合・耐用年数などをこの形で外から渡す。
/// </summary>
/// <remarks>
/// <para><b>ルールの値そのものは AccountingCore に持たない。</b> 制度値をコードに書かないという規律
/// （CLAUDE.md §2-3）の実装であり、値は制度ルールのマスタから読んで注入する。</para>
/// <para>期間が重なる定義は構築時に弾く。重なりを許すと「どちらが当たったか」が実行順に依存し、
/// 静かに間違った割合で計算される。</para>
/// </remarks>
public sealed class EffectiveDatedRuleSet<TRule>
    where TRule : IEffectiveDatedRule
{
    private readonly IReadOnlyList<TRule> _rules;

    public EffectiveDatedRuleSet(IEnumerable<TRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules.OrderBy(r => r.Period.From).ToList();

        foreach (var (earlier, later) in _rules.Zip(_rules.Skip(1)))
        {
            if (earlier.Period.Overlaps(later.Period))
            {
                throw new ArgumentException(
                    $"制度ルールの有効期間が重複している: {earlier.Period} と {later.Period}",
                    nameof(rules));
            }
        }
    }

    public IReadOnlyList<TRule> Rules => _rules;

    /// <summary>指定日に有効なルールを返す。どの期間にも当たらなければ null。</summary>
    public TRule? ResolveAt(DateOnly date) => _rules.FirstOrDefault(r => r.Period.Includes(date));

    /// <summary>版でルールを引く。過去の仕訳を再現するときは日付ではなく保存された版で引く（I-16）。</summary>
    public TRule? ResolveByVersion(RuleVersion version) => _rules.FirstOrDefault(r => r.Version == version);
}
