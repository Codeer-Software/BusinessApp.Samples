namespace BusinessApp.ServerSupport.Tests;

using BusinessApp.ServerSupport;

/// <summary>
/// マスタのコードの書式（ADR-0047）。
/// </summary>
/// <remarks>
/// <para><b>網羅の作り方は開発者の指示である</b>（2026-09-05。**必須**）——
/// 字種・長さ・見えない文字・全角半角・大文字小文字・先頭末尾・記号の連続を、
/// <b>同値分割と境界値</b>で当てる。</para>
/// <para><b>同じ検体を DDL のトリガにも当てる</b>（<c>MasterCodeGuardTests</c>）。
/// 関門と DB で受理する集合がずれると、片方だけが通す穴になる（qa/03 L-14 の型）。</para>
/// </remarks>
public sealed class MasterCodeTests
{
    /// <summary>文言に入る欄の名前（呼ぶ側が画面のラベルを渡す。docs/21 §2-6）。</summary>
    private const string Label = "科目コード";

    /// <summary>通る検体。<b>同値分割の各類から代表と境界を取る。</b></summary>
    public static TheoryData<string> Accepted => new()
    {
        // 数字の類（境界は 0 と 9）
        "0", "9", "1100",
        // 英大文字の類（境界は A と Z）
        "A", "Z", "FY18",
        // 英小文字の類（境界は a と z）
        "a", "z", "sales",
        // 区切り記号は英数字に挟まれていれば通る
        "1-2", "1_2", "10-01", "a-b_c", "1-2-3",
        // 長さの境界（下限 1・上限 20）
        "X", "12345678901234567890",
        // 大小は混ざってよい（潰さない）
        "SalesA", "salesA",
    };

    /// <summary>
    /// 断る検体と、<b>文言に必ず含まれる語</b>。
    /// </summary>
    /// <remarks>
    /// <b>文言の全文で表明しない。</b> 全文を書くと、言い回しを直すたびにテストが赤くなり、
    /// 「テストを文言に合わせる」作業になる（それは検査ではない）。
    /// <b>利用者が次に何をすればよいか分かる語</b>が入っていることだけを見る。
    /// </remarks>
    public static TheoryData<string, string> Rejected => new()
    {
        // 字種——記号（境界: 数字と英字の隣の文字コード）
        { "1/2", "使えません" },       // '/' は '0' の 1 つ前
        { "1:2", "使えません" },       // ':' は '9' の 1 つ後
        { "A@B", "使えません" },       // '@' は 'A' の 1 つ前
        { "A[B", "使えません" },       // '[' は 'Z' の 1 つ後
        { "a`b", "使えません" },       // '`' は 'a' の 1 つ前
        { "a{b", "使えません" },       // '{' は 'z' の 1 つ後
        { "1.2", "使えません" },
        { "1+2", "使えません" },
        { "1%2", "使えません" },
        // 字種——全角（見た目が同じでバイト列が違う）
        { "１１００", "使えません" },
        { "110０", "使えません" },     // 1 字だけ全角
        { "ＡＢ", "使えません" },
        { "ａｂ", "使えません" },
        // 字種——かな漢字
        { "あ", "使えません" },
        { "現金", "使えません" },
        // 字種——字の間の空白（前後は落ちるが、間は残る）
        { "1 2", "目に見えない文字" },
        { "1\t2", "目に見えない文字" },
        { "1　2", "目に見えない文字" },   // 全角スペース
        { "1\n2", "目に見えない文字" },
        // 字種——見えない字
        { "A​B", "目に見えない文字" },   // ゼロ幅スペース
        { "A﻿B", "目に見えない文字" },   // BOM
        { "A­B", "目に見えない文字" },   // ソフトハイフン
        { "AB", "目に見えない文字" },   // 制御文字
        // 字種——面外（サロゲートペア）
        { "A\U0001F642B", "使えません" },
        // 先頭・末尾の記号
        { "-100", "先頭に「-」「_」は置けません" },
        { "100-", "末尾に「-」「_」は置けません" },
        { "_100", "先頭に「-」「_」は置けません" },
        { "100_", "末尾に「-」「_」は置けません" },
        { "-", "先頭に「-」「_」は置けません" },
        { "_", "先頭に「-」「_」は置けません" },
        { "-A-", "先頭に「-」「_」は置けません" },
        // 記号の連続
        { "1--2", "続けて" },
        { "1__2", "続けて" },
        { "1-_2", "続けて" },
        { "1_-2", "続けて" },
        // 長さ（境界の上）
        { "123456789012345678901", "20 文字以内" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void 通る検体は理由を返さない(string code)
        => Assert.Null(MasterCode.DescribeProblem(Label, code));

    [Theory]
    [MemberData(nameof(Rejected))]
    public void 断る検体は利用者の語で理由を返す(string code, string expected)
    {
        var problem = MasterCode.DescribeProblem(Label, code);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    /// <summary><b>前後の空白だけは黙って落とす</b>（唯一の書き換え。ADR-0047 の決定 5）。</summary>
    [Theory]
    [InlineData(" A ", "A")]
    [InlineData("\tA\n", "A")]
    [InlineData("　A　", "A")]      // 全角スペース
    [InlineData("\r\n1100 ", "1100")]
    [InlineData("1100", "1100")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void 前後の空白だけを落とす(string? value, string expected)
        => Assert.Equal(expected, MasterCode.Normalize(value));

    /// <summary>
    /// <b>寄せない</b>——全角を半角へ、小文字を大文字へ変えない（ADR-0047 の決定 6・7）。
    /// </summary>
    [Theory]
    [InlineData("１１００")]
    [InlineData("salesA")]
    [InlineData("A100")]
    public void 空白以外は書き換えない(string value)
        => Assert.Equal(value, MasterCode.Normalize(value));

    /// <summary>
    /// <b>空は「必須」として呼ぶ側が扱う</b>ので、ここは理由を返さない。
    /// </summary>
    /// <remarks>
    /// <b>空白だけの値も同じ</b>——<see cref="MasterCode.Normalize"/> が空にするので、
    /// 呼ぶ側は正規化した値で必須を見る。
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　")]
    public void 空は理由を返さない(string? value)
        => Assert.Null(MasterCode.DescribeProblem(Label, value));

    /// <summary>
    /// <b>位置はコードポイントで数える。</b>
    /// </summary>
    /// <remarks>
    /// <c>char</c> 単位で回すと、面外の字（絵文字など）を 2 文字と数え、
    /// 文言に壊れた片割れを埋め込む。<b>この表明が、その回帰を止める。</b>
    /// </remarks>
    [Theory]
    [InlineData("A.B", "2 文字目")]
    [InlineData(".AB", "1 文字目")]
    [InlineData("AB.", "3 文字目")]
    [InlineData("A\U0001F642.", "2 文字目")]           // 面外の字が 1 文字目の次
    [InlineData("\U0001F642\U0001F642.", "1 文字目")]  // 面外の字そのもの
    public void 使えない字の位置をコードポイントで数える(string code, string expected)
        => Assert.Contains(expected, MasterCode.DescribeProblem(Label, code), StringComparison.Ordinal);

    /// <summary>
    /// <b>1 つの欄については、最初に当たった 1 つだけを返す</b>（開発者の指示。2026-09-08）。
    /// </summary>
    /// <remarks>
    /// 検査の順は 字種 → 先頭末尾 → 連続 → 長さ である。
    /// <b>順を変えると、面外の字が入ったときに長さの数え方の食い違いが表に出る</b>
    /// （C# は UTF-16 の単位、SQLite の <c>LENGTH</c> はコードポイント）。
    /// </remarks>
    [Theory]
    [InlineData("-あ", "使えません")]                       // 字種が先頭末尾より先
    [InlineData("-A--B", "先頭に「-」「_」は置けません")]                     // 先頭末尾が連続より先
    [InlineData("1--234567890123456789012", "続けて")]      // 連続が長さより先
    public void 理由は最初に当たった1つだけを返す(string code, string expected)
        => Assert.Contains(expected, MasterCode.DescribeProblem(Label, code), StringComparison.Ordinal);

    /// <summary>
    /// 文言は<b>上限の定数から作る</b>ので、定数を変えれば文言も動く。
    /// </summary>
    /// <remarks>
    /// 文言に数字を直書きすると、上限を変えたときに説明だけが古くなる
    /// （<c>CorporateNumber.FormatDescription</c> と同じ作法）。
    /// </remarks>
    [Fact]
    public void 書式の説明は上限の定数から作る()
        => Assert.Contains(
            MasterCode.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MasterCode.FormatDescription,
            StringComparison.Ordinal);

    /// <summary>
    /// <b>字種</b>の断りには、書き方の説明を添える。
    /// </summary>
    /// <remarks>
    /// <b>形（先頭末尾・連続）と長さの断りには添えない</b>——同じ規則を語を変えて 2 度言うことになり、
    /// トーストは改行できない（qa/01 D-12）ので 1 行が長くなりすぎる
    /// （2026-09-09 の自己レビュー。当初は「必ず添える」と書きながら、検体が反証の 1 ケースを避けていた）。
    /// </remarks>
    [Theory]
    [InlineData("1.2")]
    [InlineData("あ")]
    [InlineData("１")]
    public void 字種の断りには書き方の説明を添える(string code)
        => Assert.Contains(MasterCode.FormatDescription, MasterCode.DescribeProblem(Label, code), StringComparison.Ordinal);

    /// <summary>長さの断りは、上限と実際の文字数だけを言う。</summary>
    [Fact]
    public void 長さの断りには書き方の説明を添えない()
    {
        var problem = MasterCode.DescribeProblem(Label, new string('A', MasterCode.MaxLength + 1));

        Assert.Contains("20 文字以内です", problem, StringComparison.Ordinal);
        Assert.Contains("21 文字あります", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(MasterCode.FormatDescription, problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>断りは必ず欄の名前で始まる</b>（docs/21 §2-6）。
    /// </summary>
    /// <remarks>
    /// <b>裸の「コード」で始めると、どの欄を直せばよいか文から決まらない</b>——
    /// 補助科目の画面には「勘定科目」と「補助科目コード」が並ぶ（2026-09-09 の自己レビュー）。
    /// </remarks>
    [Theory]
    [InlineData("1.2")]
    [InlineData("-A")]
    [InlineData("A--B")]
    [InlineData("123456789012345678901")]
    [InlineData("A B")]
    public void 断りは欄の名前で始まる(string code)
        => Assert.StartsWith($"「{Label}」", MasterCode.DescribeProblem(Label, code), StringComparison.Ordinal);
}
