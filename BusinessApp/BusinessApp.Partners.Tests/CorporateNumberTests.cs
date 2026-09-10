namespace BusinessApp.Partners.Tests;

using BusinessApp.Partners;

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

    // --- 通らない理由の文言（取引先と自社情報の 2 か所が同じものを使う。qa/02 R12-11）---

    /// <summary>通る番号には理由が無い。</summary>
    [Fact]
    public void 検査に通る番号は理由を返さない()
        => Assert.Null(CorporateNumber.DescribeProblem(NtaExample));

    /// <summary>空なら桁の説明だけ（呼ぶ側が先に落とすので、実際にはここへ来ない）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void 空なら桁の説明を返す(string? value)
        => Assert.Equal(
            $"{CorporateNumber.FormatDescription}入力し直してください。", CorporateNumber.DescribeProblem(value));

    /// <summary>
    /// 桁が違えば、<b>いま何文字あるか</b>を言う。画面は上限で入力を止めない（docs/21 §1）ので、
    /// 貼り付けで 14 桁になった人は「13 桁です」だけでは何が起きたか分からない。
    /// </summary>
    [Theory]
    [InlineData("123456789012", 12)]
    [InlineData("12345678901234", 14)]
    [InlineData(" 12345678901234 ", 14)]
    public void 桁が違えば今の文字数を言う(string value, int count)
        => Assert.Equal(
            $"「法人番号」は数字 13 桁です。いまは {count} 文字あります。入力し直してください。",
            CorporateNumber.DescribeProblem(value));

    /// <summary>字種が違えば、<b>何文字目の何が</b>使えないかを言う（全角の数字も名指しする）。</summary>
    [Theory]
    [InlineData("１２３４５６７８９０１２３", 1, "１")]
    [InlineData("87001100059O1", 12, "O")]
    [InlineData("870011000590１", 13, "１")]
    public void 字種が違えば何文字目の何かを言う(string value, int at, string character)
        => Assert.Equal(
            $"「法人番号」の {at} 文字目の「{character}」は使えません。半角の数字で入力し直してください。",
            CorporateNumber.DescribeProblem(value));

    /// <summary>
    /// 書式は合っていて検査用数字だけが違えば、<b>打ち間違いとして</b>知らせる。
    /// </summary>
    /// <remarks>
    /// 桁の説明を返してしまうと、利用者は「13 桁あるのに 13 桁だと言われる」ことになる。
    /// </remarks>
    [Fact]
    public void 検査用数字だけが違えば打ち間違いとして知らせる()
    {
        var wrong = (NtaExample[0] == '1' ? '2' : '1') + NtaExample[1..];

        Assert.Equal(CorporateNumber.CheckDigitDescription, CorporateNumber.DescribeProblem(wrong));
    }

    /// <summary>文言に改行を入れない（トーストは改行できない。qa/01 D-12）。</summary>
    [Fact]
    public void 検査用数字の文言に改行を入れない()
        => Assert.DoesNotContain("\n", CorporateNumber.CheckDigitDescription, StringComparison.Ordinal);
}
