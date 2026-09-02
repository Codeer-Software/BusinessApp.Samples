namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

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
    public void 始期と終期が同じ一日の期間は作れる()
    {
        var oneDay = new EffectivePeriod(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 1));

        Assert.True(oneDay.Includes(new DateOnly(2026, 10, 1)));
        Assert.False(oneDay.Includes(new DateOnly(2026, 10, 2)));
    }

    [Fact]
    public void 隣接する期間は重ならない()
    {
        Assert.False(Closed.Overlaps(new EffectivePeriod(new DateOnly(2028, 10, 1), new DateOnly(2029, 9, 30))));
        Assert.False(Closed.Overlaps(new EffectivePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 30))));
    }

    [Fact]
    public void 一日でも被れば重なりと判定する()
    {
        Assert.True(Closed.Overlaps(new EffectivePeriod(new DateOnly(2028, 9, 30), null)));
        Assert.True(Closed.Overlaps(new EffectivePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 1))));
    }

    [Fact]
    public void 終期なし同士は必ず重なる()
    {
        var earlier = new EffectivePeriod(new DateOnly(2020, 1, 1), null);
        var later = new EffectivePeriod(new DateOnly(2030, 1, 1), null);

        Assert.True(earlier.Overlaps(later));
        Assert.True(later.Overlaps(earlier));
    }

    [Fact]
    public void 文字列表現は終期なしを空で表す()
    {
        Assert.Equal("2026/10/01〜2028/09/30", Closed.ToString());
        Assert.Equal("2031/10/01〜", new EffectivePeriod(new DateOnly(2031, 10, 1), null).ToString());
    }
}
