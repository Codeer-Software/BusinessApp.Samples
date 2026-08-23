namespace BusinessApp.AccountingCore.Tests.Rules;

using BusinessApp.AccountingCore.Rules;

/// <summary>制度ルールの版（I-16）。空の版を許すと、過去の再現ができない仕訳が生まれる。</summary>
public class RuleVersionTests
{
    [Fact]
    public void 版を保持して文字列にできる()
    {
        var version = new RuleVersion("transitional-deduction@2026-10-01");

        Assert.Equal("transitional-deduction@2026-10-01", version.Value);
        Assert.Equal("transitional-deduction@2026-10-01", version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void 空の版は作れない(string? value)
    {
        Assert.Throws<ArgumentException>(() => new RuleVersion(value!));
    }

    [Fact]
    public void 同じ文字列の版は等しい()
    {
        Assert.Equal(new RuleVersion("a"), new RuleVersion("a"));
        Assert.NotEqual(new RuleVersion("a"), new RuleVersion("b"));
    }
}
