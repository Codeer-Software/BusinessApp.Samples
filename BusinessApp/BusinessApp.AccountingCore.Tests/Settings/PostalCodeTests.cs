namespace BusinessApp.AccountingCore.Tests.Settings;

using BusinessApp.AccountingCore.Settings;

/// <summary>
/// 自社情報の郵便番号の書式（docs/12 §2-2）。
/// </summary>
/// <remarks>
/// <b>ここが緩むと、書式の合わない番号が帳簿と申告書に載る値として保存される。</b>
/// <b>受理する集合は DDL のトリガと同じにしてある</b>ので、
/// 片方だけ広げると「関門が通した値を DB が拒む」（qa/03 の L-14）。
/// </remarks>
public class PostalCodeTests
{
    [Theory]
    [InlineData("123-4567")]
    [InlineData("000-0000")]
    [InlineData("999-9999")]
    // **前後の空白は落としてから見る。** 貼り付けで紛れ込む（docs/21 §0）。
    [InlineData("  123-4567  ")]
    [InlineData("\t123-4567\n")]
    // **全角スペースも落ちる**（`Trim()` の既定は Unicode の空白すべて）。
    [InlineData("　123-4567　")]
    public void 書式が合っていれば通る(string value)
    {
        Assert.Null(PostalCode.DescribeProblem(value));
        Assert.True(PostalCode.IsWellFormed(value));
    }

    /// <summary>
    /// <b>長さが違うときは、いま何文字あるかまで言う。</b>
    /// </summary>
    /// <remarks>
    /// 画面は入力を止めない（docs/21 §1）ので、貼り付けた値はこの文言だけを手がかりに直すことになる。
    /// <b>空欄だけは文字数を言わない</b>——「いまは 0 文字あります」は何も足さない。
    /// </remarks>
    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("1234567", "いまは 7 文字あります。")]
    [InlineData("123-45678", "いまは 9 文字あります。")]
    [InlineData("123-4567-8", "いまは 10 文字あります。")]
    public void 長さが違えば断る(string? value, string count)
    {
        Assert.Equal(
            $"「{PostalCode.Label}」は{PostalCode.FormatDescription}です。{count}入力し直してください。",
            PostalCode.DescribeProblem(value));
        Assert.False(PostalCode.IsWellFormed(value));
    }

    /// <summary>
    /// <b>使えない字は、何文字目の何かまで言う。</b>
    /// </summary>
    /// <remarks>
    /// <b>区切りの位置も見る</b>——「1234-567」は 8 文字で長さは合うが、
    /// <c>4</c> 文字目が数字・<c>5</c> 文字目が「-」ではないので断る。
    /// <b>全角数字も断る</b>（<c>char.IsDigit</c> は真になるが、<c>char.IsAsciiDigit</c> は偽）——
    /// 見た目が同じでバイト列が違う番号が保存されると、突合が静かに外れる。
    /// </remarks>
    [Theory]
    [InlineData("12a-4567", 3, "a")]
    [InlineData("123+4567", 4, "+")]
    [InlineData("1234-567", 4, "4")]
    [InlineData("123-456x", 8, "x")]
    [InlineData("１23-4567", 1, "１")]
    [InlineData("123-456７", 8, "７")]
    public void 使えない字は位置と字を言って断る(string value, int position, string character)
    {
        // **同じ断りに欄の名前を 2 回出さない**（docs/21 §2-6）——頭で名乗っているので、
        // 書式の側は名前を含まない字を使う。
        Assert.Equal(
            $"「{PostalCode.Label}」の {position} 文字目の「{character}」は使えません。"
            + $"{PostalCode.FormatDescription}で入力し直してください。",
            PostalCode.DescribeProblem(value));
    }

    /// <summary>
    /// <b>目に見えない字は「目に見えない」と言う。</b>
    /// </summary>
    /// <remarks>
    /// <b>DDL の側でも断る</b>ので、受理する集合を合わせてある。
    /// <b>U+0000 はとくに危ない</b>——SQLite の <c>GLOB</c> は途中の NUL で止まるので、
    /// 書式が合った先にいくらでも隠せる（qa/03 の L-48 と同じ型）。
    /// </remarks>
    [Theory]
    [InlineData("123-456\u0000", 8)]
    [InlineData("12\u0000-4567", 3)]
    [InlineData("123-456\u0007", 8)]
    public void 目に見えない字は位置を言って断る(string value, int position)
    {
        Assert.Equal(
            $"「{PostalCode.Label}」の {position} 文字目に、目に見えない文字が入っています。入力し直してください。",
            PostalCode.DescribeProblem(value));
    }

    /// <summary>
    /// <b>空白を落とす以外は書き換えない</b>（ADR-0047 の線）。
    /// </summary>
    /// <remarks>
    /// 「1234567」に「-」を足して直すことはしない——
    /// <b>利用者が打った字を勝手に作り変えると、「自分が入れた値」と「保存された値」が違うことに気づけない。</b>
    /// </remarks>
    [Theory]
    [InlineData("  123-4567  ", "123-4567")]
    [InlineData("1234567", "1234567")]
    [InlineData(null, "")]
    public void 空白を落とす以外は書き換えない(string? value, string expected)
        => Assert.Equal(expected, PostalCode.Normalize(value));

    /// <summary>
    /// <b>説明に出す実例そのものが、その説明の書式を通る。</b>
    /// </summary>
    /// <remarks>
    /// <b>実例を直に書くと、桁を変えた日に説明と実例が食い違う</b>
    /// （「数字 4 桁 ＋ 「-」 ＋ 数字 4 桁（123-4567）」のような自己矛盾になる）。
    /// <b>「定数から作っているか」を同語反復で確かめても意味が無い</b>ので、
    /// <b>実例を書式に通す</b>——直書きに戻した瞬間に赤くなる。
    /// <b>プレースホルダとの一致は <c>FieldLengthConsistencyTests</c> が見る。</b>
    /// </remarks>
    [Fact]
    public void 説明に出す実例そのものが書式を通る()
    {
        Assert.True(PostalCode.IsWellFormed(PostalCode.Example), PostalCode.Example);
        Assert.Contains(PostalCode.Example, PostalCode.FormatDescription, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>実例は「123-4567」である。</b> 利用者がプレースホルダで見る字なので、字そのものを固定する
    /// （2026-09-21 のミューテーションで、後半の桁の始まりを <c>PrefixLength - 1</c> にした変異が生き残った
    /// ——「書式を通る」だけでは「123-2345」も通る）。
    /// <b>ここだけ字を直書きする</b>——<c>PostalCode</c> の注記は「桁から作る」だが、検体まで桁から作ると同語反復になる。桁を変えた日はこの検体が赤くなり、字を書き直す。
    /// </summary>
    [Fact]
    public void 実例の字は123_4567である()
    {
        Assert.Equal("123-4567", PostalCode.Example);
    }

    /// <summary>
    /// <b>基本多言語面の外の字も 1 文字と数える</b>（docs/12 §2-2。符号点で数える）。
    /// </summary>
    /// <remarks>
    /// <b><c>string.Length</c> で数えると 2 になる</b>ので、
    /// <b>8 符号単位の「𠮟𠮟𠮟𠮟」が長さの検査を通ってしまう</b>——
    /// その先で単独サロゲートが文言に埋め込まれる。
    /// <b>SQLite の <c>LENGTH()</c> は同じ字を 1 と数える</b>ので、数え方を揃えてある。
    /// </remarks>
    [Fact]
    public void 基本多言語面の外の字も一文字と数える()
    {
        var text = string.Concat(Enumerable.Repeat("\U0001F642", 4));

        Assert.Equal(8, text.Length);
        Assert.Equal(
            $"「{PostalCode.Label}」は{PostalCode.FormatDescription}です。いまは 4 文字あります。入力し直してください。",
            PostalCode.DescribeProblem(text));
    }

    /// <summary>
    /// <b>長さが合っていても、基本多言語面の外の字は字として断る。</b>
    /// </summary>
    /// <remarks>
    /// <b>文言に出すのは符号点 1 つ分である</b>（単独サロゲートを出さない）。
    /// </remarks>
    [Fact]
    public void 長さが合っていても外の字は断る()
    {
        var text = "\U0001F642\U0001F642\U0001F642-4567";

        Assert.Equal(
            $"「{PostalCode.Label}」の 1 文字目の「\U0001F642」は使えません。"
            + $"{PostalCode.FormatDescription}で入力し直してください。",
            PostalCode.DescribeProblem(text));
    }
}
