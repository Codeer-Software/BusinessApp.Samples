namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>金額の型（docs/10 §3）。円未満を持てないことと、端数処理が明示的であることを固定する。</summary>
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
    public void 正の値かどうかを判定できる()
    {
        Assert.True(Yen.From(1).IsPositive);
        Assert.False(Yen.From(0).IsPositive);
        Assert.False(Yen.From(-1).IsPositive);
    }

    [Fact]
    public void 末尾の零で等値性が壊れない()
    {
        Assert.Equal(Yen.From(100m), Yen.From(100.00m));
        Assert.Equal(Yen.From(100m).GetHashCode(), Yen.From(100.00m).GetHashCode());
    }

    [Fact]
    public void 加減算ができる()
    {
        Assert.Equal(Yen.From(300), Yen.From(100) + Yen.From(200));
        Assert.Equal(Yen.From(-100), Yen.From(100) - Yen.From(200));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1234, "1,234")]
    [InlineData(-1234, "-1,234")]
    [InlineData(1234567, "1,234,567")]
    public void 文字列表現は小数点を持たず_3_桁で区切る(long value, string expected)
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
