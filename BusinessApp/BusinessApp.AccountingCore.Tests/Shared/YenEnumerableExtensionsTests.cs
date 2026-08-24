namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>金額の列の合計。</summary>
public class YenEnumerableExtensionsTests
{
    [Fact]
    public void 合計できる()
    {
        Assert.Equal(Yen.From(600), new[] { Yen.From(100), Yen.From(200), Yen.From(300) }.Sum());
    }

    [Fact]
    public void 空の列の合計は零になる()
    {
        Assert.Equal(Yen.Zero, Array.Empty<Yen>().Sum());
    }

    [Fact]
    public void nullの列は合計できない()
    {
        Assert.Throws<ArgumentNullException>(() => ((IEnumerable<Yen>)null!).Sum());
    }
}
