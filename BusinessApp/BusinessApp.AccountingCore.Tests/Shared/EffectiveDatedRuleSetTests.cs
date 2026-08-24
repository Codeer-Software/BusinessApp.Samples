namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 有効期間つき制度ルールの解決。税率・控除割合・耐用年数がこの上に載るので、
/// <b>ここが静かに間違うと制度ロジック全体が嘘になる</b>。
/// </summary>
public class EffectiveDatedRuleSetTests
{
    private sealed record TestRule(EffectivePeriod Period, RuleVersion Version) : IEffectiveDatedRule;

    private static TestRule Rule(string from, string? to, string version)
        => new(new EffectivePeriod(DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to)), new RuleVersion(version));

    [Fact]
    public void 日付でルールを引ける()
    {
        var set = new EffectiveDatedRuleSet<TestRule>([
            Rule("2026-04-01", "2026-09-30", "v1"),
            Rule("2026-10-01", null, "v2"),
        ]);

        Assert.Equal(new RuleVersion("v1"), set.ResolveAt(new DateOnly(2026, 9, 30))?.Version);
        Assert.Equal(new RuleVersion("v2"), set.ResolveAt(new DateOnly(2026, 10, 1))?.Version);
        Assert.Null(set.ResolveAt(new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public void 版でルールを引ける()
    {
        var set = new EffectiveDatedRuleSet<TestRule>([Rule("2026-04-01", null, "v1")]);

        Assert.NotNull(set.ResolveByVersion(new RuleVersion("v1")));
        Assert.Null(set.ResolveByVersion(new RuleVersion("v9")));
    }

    [Fact]
    public void 入力順に関わらず有効期間の早い順に並ぶ()
    {
        var set = new EffectiveDatedRuleSet<TestRule>([
            Rule("2026-10-01", null, "v2"),
            Rule("2026-04-01", "2026-09-30", "v1"),
        ]);

        Assert.Equal(
            new[] { new RuleVersion("v1"), new RuleVersion("v2") },
            set.Rules.Select(r => r.Version));
    }

    [Fact]
    public void 有効期間が重なるルールは受け付けない()
    {
        Assert.Throws<ArgumentException>(() => new EffectiveDatedRuleSet<TestRule>([
            Rule("2026-04-01", "2026-09-30", "v1"),
            Rule("2026-09-30", null, "v2"),
        ]));
    }

    [Fact]
    public void 空の集合は何も解決しない()
    {
        var set = new EffectiveDatedRuleSet<TestRule>([]);

        Assert.Empty(set.Rules);
        Assert.Null(set.ResolveAt(new DateOnly(2026, 5, 20)));
        Assert.Null(set.ResolveByVersion(new RuleVersion("v1")));
    }

    [Fact]
    public void nullでは作れない()
    {
        Assert.Throws<ArgumentNullException>(() => new EffectiveDatedRuleSet<TestRule>(null!));
    }
}
