namespace BusinessApp.AccountingCore.Tests.Primitives;

using BusinessApp.AccountingCore.Primitives;

/// <summary>金額の型（docs/04 §3）。円未満を持てないことと、端数処理が明示的であることを固定する。</summary>
public class YenTests
{
    [Theory]
    [InlineData("0.5")]
    [InlineData("-0.5")]
    [InlineData("100.01")]
    public void 小数部のある金額は作れない(string value)
    {
        Assert.Throws<ArgumentException>(() => Yen.From(decimal.Parse(value)));
    }

    [Fact]
    public void 末尾の零で等値性が壊れない()
    {
        Assert.Equal(Yen.From(100m), Yen.From(100.00m));
        Assert.Equal(Yen.From(100m).GetHashCode(), Yen.From(100.00m).GetHashCode());
    }

    [Fact]
    public void 加減算と合計ができる()
    {
        Assert.Equal(Yen.From(300), Yen.From(100) + Yen.From(200));
        Assert.Equal(Yen.From(-100), Yen.From(100) - Yen.From(200));
        Assert.Equal(Yen.From(-100), -Yen.From(100));
        Assert.Equal(Yen.From(600), new[] { Yen.From(100), Yen.From(200), Yen.From(300) }.Sum());
        Assert.Equal(Yen.Zero, Array.Empty<Yen>().Sum());
    }

    [Fact]
    public void nullの列は合計できない()
    {
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<Yen>)null!).Sum());
    }

    [Fact]
    public void 符号を判定できる()
    {
        Assert.True(Yen.From(1).IsPositive);
        Assert.False(Yen.From(0).IsPositive);
        Assert.False(Yen.From(-1).IsPositive);

        Assert.True(Yen.From(-1).IsNegative);
        Assert.False(Yen.From(0).IsNegative);
        Assert.False(Yen.From(1).IsNegative);

        Assert.True(Yen.Zero.IsZero);
        Assert.False(Yen.From(1).IsZero);
    }

    [Fact]
    public void 大小を比較できる()
    {
        var small = Yen.From(100);
        var large = Yen.From(200);

        Assert.True(small < large);
        Assert.False(large < small);
        Assert.True(large > small);
        Assert.False(small > large);
        Assert.True(small <= large);
        Assert.True(small <= Yen.From(100));
        Assert.False(large <= small);
        Assert.True(large >= small);
        Assert.True(large >= Yen.From(200));
        Assert.False(small >= large);

        Assert.True(small.CompareTo(large) < 0);
        Assert.True(large.CompareTo(small) > 0);
        Assert.Equal(0, small.CompareTo(Yen.From(100)));
    }

    [Fact]
    public void 並べ替えに使える()
    {
        var sorted = new[] { Yen.From(300), Yen.From(100), Yen.From(200) }.Order().ToArray();

        Assert.Equal(new[] { Yen.From(100), Yen.From(200), Yen.From(300) }, sorted);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1234, "1234")]
    [InlineData(-1234, "-1234")]
    public void 文字列表現は小数点を持たない(long value, string expected)
    {
        Assert.Equal(expected, Yen.From(value).ToString());
    }

    [Theory]
    [InlineData(RoundingMode.Truncate, 5443)]
    [InlineData(RoundingMode.Round, 5444)]
    [InlineData(RoundingMode.Ceiling, 5444)]
    public void 比率の乗算は端数処理の指定を要求する(RoundingMode mode, long expected)
    {
        // 7,777 × 0.7 = 5,443.9
        Assert.Equal(Yen.From(expected), Yen.From(7_777).Multiply(0.7m, mode));
    }

    [Theory]
    [InlineData(RoundingMode.Truncate, -5443)]
    [InlineData(RoundingMode.Round, -5444)]
    [InlineData(RoundingMode.Ceiling, -5444)]
    public void 負の金額の端数処理は絶対値に対して同じ向きに働く(RoundingMode mode, long expected)
    {
        Assert.Equal(Yen.From(expected), Yen.From(-7_777).Multiply(0.7m, mode));
    }

    [Fact]
    public void 四捨五入は五を切り上げる()
    {
        // 1,001 × 0.5 = 500.5
        Assert.Equal(Yen.From(501), Yen.From(1_001).Multiply(0.5m, RoundingMode.Round));
    }
}
