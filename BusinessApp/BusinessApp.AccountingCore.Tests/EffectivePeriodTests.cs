namespace BusinessApp.AccountingCore.Tests;

using BusinessApp.AccountingCore.Primitives;

/// <summary>有効期間。両端を含むこと・終期なしを扱えることを固定する。</summary>
public class EffectivePeriodTests
{
    private static readonly EffectivePeriod Closed =
        new(new DateOnly(2026, 10, 1), new DateOnly(2028, 9, 30));

    [Theory]
    [InlineData("2026-09-30", false)]
    [InlineData("2026-10-01", true)]
    [InlineData("2028-09-30", true)]
    [InlineData("2028-10-01", false)]
    public void 両端を含む(string date, bool expected)
    {
        Assert.Equal(expected, Closed.Includes(DateOnly.Parse(date)));
    }

    [Fact]
    public void 終期なしは以後すべてを含む()
    {
        var openEnded = new EffectivePeriod(new DateOnly(2031, 10, 1), null);
        Assert.False(openEnded.Includes(new DateOnly(2031, 9, 30)));
        Assert.True(openEnded.Includes(new DateOnly(2031, 10, 1)));
        Assert.True(openEnded.Includes(new DateOnly(2999, 12, 31)));
    }

    [Fact]
    public void 終期が始期より前なら作れない()
    {
        Assert.Throws<ArgumentException>(
            () => new EffectivePeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 30)));
    }

    [Fact]
    public void 隣接する期間は重ならない()
    {
        var next = new EffectivePeriod(new DateOnly(2028, 10, 1), new DateOnly(2029, 9, 30));
        Assert.False(Closed.Overlaps(next));
        Assert.True(Closed.Overlaps(new EffectivePeriod(new DateOnly(2028, 9, 30), null)));
    }
}
