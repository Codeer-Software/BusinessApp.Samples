namespace BusinessApp.AccountingCore.Shared;

/// <summary>
/// 制度ルールの版（docs/15_実装の原則.md §2）。
/// 仕訳明細に適用した版を固定して保存し、後日マスタを更新しても過去を再計算しない（I-16）。
/// </summary>
public readonly record struct RuleVersion
{
    public RuleVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("制度ルールの版は空にできない", nameof(value));
        }
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
