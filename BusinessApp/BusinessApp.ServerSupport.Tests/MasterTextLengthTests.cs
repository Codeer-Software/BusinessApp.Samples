namespace BusinessApp.ServerSupport.Tests;

using System.Globalization;

using BusinessApp.ServerSupport;

/// <summary>
/// 文字の欄の上限（docs/12 §2-2。2026-09-13 の決着）。
/// </summary>
/// <remarks>
/// <para><b>網羅の作り方は開発者の指示である</b>（2026-09-05。規則の正典は docs/31 §5）——
/// <b>同値分割と境界値</b>で当てる。長さの類は「上限未満・上限ちょうど・上限＋1」の 3 つ、
/// 字の類は「基本多言語面の中・外・結合するもの」である。</para>
/// <para><b>同じ検体を DDL のトリガにも当てる</b>（<c>TextLengthGuardTests</c>）。
/// <b>関門と DB で数え方がずれると、画面が断った値を DB が通す</b>（qa/03 L-14 の型）——
/// ここは <b>C# 側が何と数えるか</b>だけを見て、突き合わせはあちらが持つ。</para>
/// </remarks>
public sealed class MasterTextLengthTests
{
    /// <summary>文言に入る欄の名前（呼ぶ側が画面のラベルを渡す。docs/21 §2-6）。</summary>
    private const string Label = "科目名";

    /// <summary>
    /// <b>上限ちょうどまでは通し、1 文字超えたら断る。</b>
    /// </summary>
    /// <remarks>
    /// <b>両端を撃たないと <c>&gt;</c> と <c>&gt;=</c> の取り違えが見えない。</b>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(29)]
    [InlineData(30)]
    public void 上限までの長さは通す(int length)
        => Assert.Null(MasterTextLength.DescribeProblem(Label, new string('あ', length), 30));

    /// <summary>上限を超えたら断る。</summary>
    [Theory]
    [InlineData(31)]
    [InlineData(60)]
    public void 上限を超えたら断る(int length)
        => Assert.NotNull(MasterTextLength.DescribeProblem(Label, new string('あ', length), 30));

    /// <summary>
    /// 断りは<b>欄の名前・上限・いまの文字数</b>の 3 つを言う。
    /// </summary>
    /// <remarks>
    /// <b>上限だけを言われても、貼り付けた利用者にはどれだけ削ればよいか分からない</b>
    /// （docs/21 §2）。<b>3 つとも入っていることを 1 本で見る</b>——
    /// 文言を丸ごと比べると、語順を変えただけで赤くなる。
    /// </remarks>
    [Fact]
    public void 断りは欄の名前と上限といまの文字数を言う()
    {
        var problem = MasterTextLength.DescribeProblem("取引先名", new string('あ', 105), 100);

        Assert.Equal(
            "「取引先名」は 100 文字以内です。いまは 105 文字あります。短くして入力し直してください。",
            problem);
    }

    /// <summary>
    /// <b>空と <c>null</c> は長さの話ではない。</b>
    /// </summary>
    /// <remarks>
    /// <b>必須は画面の <c>IsRequired</c> と DB の <c>NOT NULL</c> の仕事である。</b>
    /// <b>長さの関門が「空です」と言い出すと、同じ欄を 2 つの関門が別々の文言で断る。</b>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 空と_null_は断らない(string? value)
    {
        Assert.Null(MasterTextLength.DescribeProblem(Label, value, 30));
        Assert.Equal(0, MasterTextLength.Count(value));
    }

    /// <summary>
    /// <b>Unicode の符号点で数える</b>（docs/12 §2-2）。
    /// </summary>
    /// <remarks>
    /// <para><b><c>string.Length</c> は UTF-16 の符号単位を数える</b>ので、
    /// 基本多言語面の外の字（𠮟・🙂）を 2 と数える。<b>SQLite の <c>LENGTH()</c> は 1 と数える</b>ので、
    /// そのままでは<b>関門が断った値を DDL が通す</b>。</para>
    /// <para><b>符号単位と食い違う検体でなければ、この取り違えは見えない</b>——
    /// だから <c>string.Length</c> と答えが割れる字だけを並べる。</para>
    /// </remarks>
    [Theory]
    [InlineData("\U0001F642", 1)]                 // 🙂（サロゲートペア 1 つ）
    [InlineData("\U00020B9F", 1)]                 // 𠮟（追加漢字面）
    [InlineData("\U0001F642\U0001F642", 2)]
    [InlineData("あ\U0001F642い", 3)]
    public void 基本多言語面の外の字は一文字と数える(string value, int expected)
    {
        Assert.Equal(expected, MasterTextLength.Count(value));
        Assert.NotEqual(value.Length, MasterTextLength.Count(value));
    }

    /// <summary>
    /// <b>書記素では数えない</b>（docs/12 §2-2）。
    /// </summary>
    /// <remarks>
    /// <para>結合文字・異体字セレクタ・国旗は、<b>見た目 1 文字でも符号点は複数</b>である。
    /// <b>SQLite に書記素を数える手立てが無い</b>ので、こちらも符号点に揃える——
    /// <b>2 つの守りが同じ数を返すことのほうが、見た目の直感より大事である</b>。</para>
    /// <para><b>この表明は「そう決めた」ことの記録である。</b>
    /// 書記素で数えるように変えたくなった日に、ここが赤くなって
    /// <b>DDL 側の手当てが要ることを思い出させる</b>。</para>
    /// </remarks>
    [Theory]
    [InlineData("が", 2)]               // か + 結合濁点（見た目は「が」）
    [InlineData("葛\U000E0100", 2)]                // 葛 + 異体字セレクタ
    [InlineData("\U0001F1EF\U0001F1F5", 2)]        // 日本の国旗（地域指示記号 2 つ）
    public void 書記素ではなく符号点で数える(string value, int expected)
        => Assert.Equal(expected, MasterTextLength.Count(value));

    /// <summary>
    /// <b>全角と半角を区別しない</b>（docs/12 §2-2）。
    /// </summary>
    /// <remarks>
    /// 市販でも freee・MF・弥生 Next が「全角・半角を問わず」で、
    /// <b>半角換算の桁数を使うのはデスクトップ製品だけ</b>である。
    /// </remarks>
    [Fact]
    public void 全角と半角を区別しない()
    {
        // **値まで表明する。** 2 つを比べるだけだと、`Count` が常に 0 を返しても緑になる
        // （qa/03 の L-46 と同じ形。自己レビューのラウンド 94）。
        Assert.Equal(3, MasterTextLength.Count("ABC"));
        Assert.Equal(3, MasterTextLength.Count("ＡＢＣ"));
    }

    /// <summary>
    /// <b>上限は呼ぶ側が決める。</b>
    /// </summary>
    /// <remarks>
    /// <b>3 つの定数が docs/12 §2-2 の表の写しである</b>（DDL とデザインとの一致は
    /// <c>FieldLengthConsistencyTests</c> が見る）。<b>ここで値を固定しておくと、
    /// 定数を書き換えた回に、どこを直すべきかの入口が 1 つ残る。</b>
    /// </remarks>
    [Fact]
    public void 上限は表の写しである()
    {
        Assert.Equal(30, MasterTextLength.MasterName);
        Assert.Equal(100, MasterTextLength.PartnerName);
        Assert.Equal(200, MasterTextLength.Address);
    }

    /// <summary>
    /// <b>前後の空白を落とす</b>（docs/21 §0）。
    /// </summary>
    /// <remarks>
    /// <b>落とさないと、画面で数えた字数と断りの「いまは N 文字」が合わない</b>——
    /// Excel から貼った値には前後の空白が付く。<c>Trim()</c> の既定は
    /// <b>全角スペース・タブ・改行も落とす</b>（<see cref="MasterCode.Normalize"/> と同じ）。
    /// </remarks>
    [Theory]
    [InlineData("  あ  ", "あ")]
    [InlineData("\u3000あ\u3000", "あ")]        // 全角の空白
    [InlineData("\tあ\n", "あ")]                // タブと改行
    [InlineData("あ い", "あ い")]              // 字の間の空白は落とさない
    [InlineData("", "")]
    [InlineData(null, null)]
    public void 前後の空白だけを落とす(string? value, string? expected)
        => Assert.Equal(expected, MasterTextLength.Normalize(value));

    /// <summary>
    /// <b>U+0000 は断る</b>——DDL が断るからである。
    /// </summary>
    /// <remarks>
    /// <para><b>受理する集合を DDL と同じにする</b>（qa/03 の L-14）。
    /// SQLite の <c>LENGTH()</c> は<b>途中の U+0000 で止まる</b>ので、
    /// DDL は NUL を含む TEXT を「数えられない値」として断る。
    /// <b>関門が通すと、利用者には定型文しか出ない</b>（qa/03 の L-28）。</para>
    /// <para><b>何文字目かまで言う。</b> 目に見えない字は、言われないと探せない。</para>
    /// </remarks>
    [Fact]
    public void 目に見えない文字は位置を言って断る()
    {
        Assert.Equal(
            "「科目名」の 3 文字目に、目に見えない文字が入っています。入力し直してください。",
            MasterTextLength.DescribeProblem(Label, "あい\0う", 30));
    }

    /// <summary>
    /// <b>NUL は長さより先に断る。</b>
    /// </summary>
    /// <remarks>
    /// <b>NUL の後ろが長くても「長すぎます」とは言わない</b>——
    /// <b>直し方が違う</b>（削るのではなく、その字を取り除く）。
    /// </remarks>
    [Fact]
    public void 長すぎてもNULが先に出る()
    {
        Assert.Contains(
            "目に見えない文字",
            MasterTextLength.DescribeProblem(Label, "あ\0" + new string('い', 100), 30),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 文言の数字は<b>不変文化で書く</b>。
    /// </summary>
    /// <remarks>
    /// <b>桁区切りの入る文化では「1,000 文字以内」になり、DDL の数と見比べられなくなる。</b>
    /// </remarks>
    [Fact]
    public void 文言の数字は桁区切りを付けない()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains(
                "は 1000 文字以内です。いまは 1001 文字あります。",
                MasterTextLength.DescribeProblem(Label, new string('あ', 1001), 1000),
                StringComparison.Ordinal);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
