namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 閉じた日付の範囲。会計年度・会計期間に使う。
/// 「終わらない会計期間」を型として作れないことが、この型を分けた理由である。
/// </summary>
public class DateRangeTests
{
    private static readonly DateRange May = new(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));

    [Theory]
    [InlineData("2026-04-30", false)]
    [InlineData("2026-05-01", true)]
    [InlineData("2026-05-31", true)]
    [InlineData("2026-06-01", false)]
    public void 両端を含む(string date, bool expected)
    {
        Assert.Equal(expected, May.Includes(DateOnly.Parse(date)));
    }

    [Fact]
    public void 終わりが始まりより前なら作れない()
    {
        Assert.Throws<ArgumentException>(
            () => new DateRange(new DateOnly(2026, 5, 31), new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void 一日だけの範囲は作れる()
    {
        var oneDay = new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 1));

        Assert.True(oneDay.Includes(new DateOnly(2026, 5, 1)));
        Assert.False(oneDay.Includes(new DateOnly(2026, 5, 2)));
    }

    [Fact]
    public void 隣接する範囲は重ならない()
    {
        Assert.False(May.Overlaps(new DateRange(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30))));
        Assert.False(May.Overlaps(new DateRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30))));
    }

    [Fact]
    public void 一日でも被れば重なりと判定する()
    {
        Assert.True(May.Overlaps(new DateRange(new DateOnly(2026, 5, 31), new DateOnly(2026, 6, 30))));
        Assert.True(May.Overlaps(new DateRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1))));
    }

    [Fact]
    public void 文字列表現は両端を出す()
    {
        Assert.Equal("2026-05-01〜2026-05-31", May.ToString());
    }
}
