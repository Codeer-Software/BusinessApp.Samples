namespace BusinessApp.AccountingCore.Rules;

using BusinessApp.AccountingCore.Primitives;

/// <summary>
/// 有効期間を持つ制度ルール。税率・控除割合・耐用年数などがこれを実装する。
/// </summary>
public interface IEffectiveDatedRule
{
    /// <summary>このルールが有効な期間。</summary>
    EffectivePeriod Period { get; }

    /// <summary>ルールの版。仕訳に固定して保存し、過去を再計算しないために使う（I-16）。</summary>
    RuleVersion Version { get; }
}
