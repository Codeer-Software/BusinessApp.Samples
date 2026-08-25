namespace BusinessApp.AccountingCore.Tests.Partners;

using BusinessApp.AccountingCore.Partners;

public class InvoiceRegistrationNumberTests
{
    [Theory]
    [InlineData("T1234567890123")]
    [InlineData("T0000000000000")]
    [InlineData("  T1234567890123  ")]   // 貼り付けの空白は書式違反にしない
    public void 正しい書式を通す(string value)
        => Assert.True(InvoiceRegistrationNumber.IsWellFormed(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234567890123")]        // T が無い
    [InlineData("t1234567890123")]       // 小文字は受け付けない（2 通りの文字列で保存させない）
    [InlineData("T123456789012")]        // 12 桁
    [InlineData("T12345678901234")]      // 14 桁
    [InlineData("T123456789012X")]       // 数字以外
    [InlineData("T１２３４５６７８９０１２３")]  // 全角数字（char.IsDigit は真になる）
    [InlineData("T-123456789012")]
    public void 誤った書式を弾く(string? value)
        => Assert.False(InvoiceRegistrationNumber.IsWellFormed(value));

    [Fact]
    public void 前後の空白を落とした姿を返す()
    {
        Assert.Equal("T1234567890123", InvoiceRegistrationNumber.Normalize("  T1234567890123 "));
        Assert.Equal(string.Empty, InvoiceRegistrationNumber.Normalize(null));
    }

    /// <summary>
    /// <b>利用者に見せる説明が、実際に通す形と食い違わない。</b>
    /// 説明どおりの番号を作って通し、1 桁ずらしたら弾かれることを見る。
    /// </summary>
    [Fact]
    public void 書式の説明どおりの番号が通る()
    {
        var described = "T" + new string('0', InvoiceRegistrationNumber.DigitCount);

        Assert.True(InvoiceRegistrationNumber.IsWellFormed(described));
        Assert.False(InvoiceRegistrationNumber.IsWellFormed(described + "0"));
        Assert.False(InvoiceRegistrationNumber.IsWellFormed(described[..^1]));
        Assert.Equal(InvoiceRegistrationNumber.Length, described.Length);
    }

    /// <summary>文言は改行を持たない（トーストは改行できない。qa/01 D-12）。</summary>
    [Fact]
    public void 書式の説明に改行を入れない()
        => Assert.DoesNotContain("\n", InvoiceRegistrationNumber.FormatDescription, StringComparison.Ordinal);
}
