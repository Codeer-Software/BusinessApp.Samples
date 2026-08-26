namespace BusinessApp.AccountingCore.Tests.Partners;

using BusinessApp.AccountingCore.Partners;

public class CorporateNumberTests
{
    /// <summary>
    /// 国税庁が示している計算例（docs/research/2026-08-25_取引先の識別番号と公表システム.md §1-1）。
    /// 会社法人等番号 <c>700110005901</c> の検査用数字は <c>8</c>。
    /// </summary>
    private const string NtaExample = "8700110005901";

    [Theory]
    [InlineData("1234567890123")]
    [InlineData("  1234567890123  ")]   // 貼り付けの空白は書式違反にしない
    [InlineData("0000000000000")]
    public void 正しい書式を通す(string value)
        => Assert.True(CorporateNumber.IsWellFormed(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456789012")]         // 12 桁
    [InlineData("12345678901234")]       // 14 桁
    [InlineData("123456789012X")]        // 数字以外
    [InlineData("T123456789012")]        // 登録番号を貼り付けた形
    [InlineData("１２３４５６７８９０１２３")]  // 全角数字（char.IsDigit は真になる）
    [InlineData("1234-5678-90123")]
    public void 誤った書式を弾く(string? value)
        => Assert.False(CorporateNumber.IsWellFormed(value));

    [Fact]
    public void 前後の空白を落とした姿を返す()
    {
        Assert.Equal("1234567890123", CorporateNumber.Normalize("  1234567890123 "));
        Assert.Equal(string.Empty, CorporateNumber.Normalize(null));
    }

    /// <summary>国税庁の計算例。<b>算式を取り違えても大半の番号は通ってしまう</b>ので、例で固定する。</summary>
    [Fact]
    public void 国税庁の計算例の検査用数字を認める()
        => Assert.True(CorporateNumber.HasValidCheckDigit(NtaExample));

    /// <summary>
    /// <b>先頭 1 桁を総当たりして、通るのが 1 つだけであることを見る。</b>
    /// 「例が通る」だけだと、常に真を返す実装でも緑になる。
    /// </summary>
    [Fact]
    public void 検査用数字が合う先頭の数字は一つしかない()
    {
        var accepted = Enumerable.Range(0, 10)
            .Select(digit => $"{digit}{NtaExample[1..]}")
            .Where(CorporateNumber.HasValidCheckDigit)
            .ToList();

        Assert.Equal([NtaExample], accepted);
    }

    /// <summary>
    /// 余りが 0 のとき検査用数字は 9 になる（施行令の「一から九までの整数」と整合する）。
    /// <c>9 - 0</c> を <c>0</c> に丸める実装だと、この 1 件だけが落ちる。
    /// </summary>
    [Fact]
    public void 余りが零なら検査用数字は九になる()
    {
        Assert.True(CorporateNumber.HasValidCheckDigit("9" + new string('0', CorporateNumber.BaseNumberLength)));
        Assert.False(CorporateNumber.HasValidCheckDigit("0" + new string('0', CorporateNumber.BaseNumberLength)));
    }

    /// <summary>
    /// <b>桁を数える向きを取り違えていないか。</b> 最下位から数える算式なので、
    /// 基礎番号を逆から並べた番号は（回文でない限り）別の検査用数字になる。
    /// </summary>
    [Fact]
    public void 基礎番号を逆に並べると検査用数字が変わる()
    {
        var reversed = new string(NtaExample[1..].Reverse().ToArray());

        Assert.False(CorporateNumber.HasValidCheckDigit(NtaExample[0] + reversed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("870011000590")]         // 12 桁
    [InlineData("87001100059010")]       // 14 桁
    [InlineData("870011000590X")]
    public void 書式が崩れていれば検査用数字も通さない(string? value)
        => Assert.False(CorporateNumber.HasValidCheckDigit(value));

    /// <summary>
    /// <b>利用者に見せる説明に、実際の桁数が入っている。</b>
    /// 説明を手書きしていると、桁を変えたときに説明だけが古くなる。
    /// </summary>
    [Fact]
    public void 書式の説明は実際の桁数を言う()
    {
        Assert.Contains(
            CorporateNumber.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CorporateNumber.FormatDescription,
            StringComparison.Ordinal);
    }

    /// <summary>文言は改行を持たない（トーストは改行できない。qa/01 D-12）。</summary>
    [Fact]
    public void 書式の説明に改行を入れない()
        => Assert.DoesNotContain("\n", CorporateNumber.FormatDescription, StringComparison.Ordinal);
}
