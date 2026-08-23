namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>端数処理（docs/06 §4）。用途ごとに方法が違うため、既定に流れないことを確かめる。</summary>
public class RoundingTests
{
    [Theory]
    [InlineData(RoundingMode.Truncate, 100.9, 100)]
    [InlineData(RoundingMode.Truncate, 100.1, 100)]
    [InlineData(RoundingMode.Round, 100.5, 101)]
    [InlineData(RoundingMode.Round, 100.4, 100)]
    [InlineData(RoundingMode.Ceiling, 100.1, 101)]
    [InlineData(RoundingMode.Ceiling, 100.0, 100)]
    public void 正の値を処理する(RoundingMode mode, double value, int expected)
    {
        Assert.Equal(expected, Rounding.Apply((decimal)value, mode));
    }

    [Theory]
    [InlineData(RoundingMode.Truncate, -100.9, -100)]
    [InlineData(RoundingMode.Round, -100.5, -101)]
    [InlineData(RoundingMode.Ceiling, -100.1, -101)]
    public void 負の値は絶対値に対して同じ向きに働く(RoundingMode mode, double value, int expected)
    {
        Assert.Equal(expected, Rounding.Apply((decimal)value, mode));
    }

    [Fact]
    public void 未知の方法は受け付けない()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Rounding.Apply(100.5m, (RoundingMode)999));
    }
}
