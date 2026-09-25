namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;

/// <summary>
/// 明細の 1 行だけで決まる規則（保存の手前の網と、計上の検証が共有する）。
/// </summary>
/// <remarks>
/// <b>境界は DDL に合わせてある。</b> ここが DDL より広いと、関門が
/// 「DB に拒まれる値」を保存へ渡してしまう（qa/03 L-14 の型）。狭いと、
/// 正しい値を持つ利用者だけが計上できなくなる。<b>両側を表明する。</b>
/// </remarks>
public class JournalLineRulesTests
{
    /// <summary>
    /// <c>journal_lines.amount</c> は <c>CHECK (amount &gt; 0 AND typeof(amount) = 'integer')</c>。
    /// SQLite の INTEGER は 64 ビットなので、<see cref="long"/> が上限そのものである。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1000000)]
    public void 金額は一円以上の整数を通す(long amount)
    {
        Assert.True(JournalLineRules.IsStorableAmount(amount));
    }

    [Fact]
    public void 金額は上限ちょうどを通し_その一つ上を拒む()
    {
        Assert.True(JournalLineRules.IsStorableAmount(long.MaxValue));
        Assert.False(JournalLineRules.IsStorableAmount((decimal)long.MaxValue + 1));
    }

    /// <remarks>
    /// <c>decimal</c> は属性の引数にできないので <c>double</c> で受けて変換する。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(-0.5)]
    public void 金額は正でない値と端数のある値を拒む(double amount)
    {
        Assert.False(JournalLineRules.IsStorableAmount((decimal)amount));
    }

    /// <summary>
    /// 行番号の上限は DB ではなく<b>こちらの都合</b>（差し戻しに添える <c>int?</c> に収める）。
    /// </summary>
    [Fact]
    public void 行番号は_int_の上限ちょうどを通し_その一つ上を拒む()
    {
        Assert.True(JournalLineRules.IsStorableLineNo(int.MaxValue));
        Assert.False(JournalLineRules.IsStorableLineNo((decimal)int.MaxValue + 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void 行番号は一以上の整数を通す(int lineNo)
    {
        Assert.True(JournalLineRules.IsStorableLineNo(lineNo));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void 行番号は正でない値と端数のある値を拒む(double lineNo)
    {
        Assert.False(JournalLineRules.IsStorableLineNo((decimal)lineNo));
    }

    /// <summary>
    /// <b>文言をここが持つ理由を、テストでも固定する。</b> 同じ原因に 2 通りの言い方があると、
    /// どちらの層が先に捕まえたかで利用者に出る言葉が変わる。
    /// </summary>
    [Fact]
    public void 差し戻しの文言に改行を入れない()
    {
        string[] messages =
        [
            JournalLineRules.AmountNotPositive, JournalLineRules.AmountHasFraction,
            JournalLineRules.AmountTooLarge, JournalLineRules.LineNoNotStorable,
            JournalLineRules.TaxCategoryMissing, JournalLineRules.LineNoMissing,
            JournalLineRules.DebitCreditMissing, JournalLineRules.AccountMissing,
            JournalLineRules.AmountMissing, JournalLineRules.TransactionDateMissing,
            JournalLineRules.PostingDateMissing, JournalLineRules.FiscalYearMissing,
            JournalLineRules.OriginalEntryNotEditable, JournalLineRules.ChangedByOthers,
            JournalLineRules.DeletedByOthers, JournalLineRules.AlreadyDeletedByOthers,
            JournalLineRules.LineNoDuplicatedAt(3), JournalLineRules.LinesDeletedByOthers(2),
            JournalLineRules.LinesChangedByOthers(2), JournalLineRules.PostedByOthers, JournalLineRules.PostedByOthersOnDelete,
        ];

        // トースト内の文字列は改行できない（qa/01 D-12）。
        Assert.All(messages, m => Assert.DoesNotContain("\n", m, StringComparison.Ordinal));

        // です・ます調（docs/21 §2-1）。
        Assert.All(messages, m => Assert.EndsWith("。", m, StringComparison.Ordinal));
    }

    /// <summary>件数と番号は文に埋める（行は指せないので、件数で束ねる）。</summary>
    [Fact]
    public void 消えた明細と変えられた明細は件数で_重なった番号は番号で言う()
    {
        Assert.StartsWith("明細 2 行が、あなたが開いたあとに別の人に削除されています。", JournalLineRules.LinesDeletedByOthers(2), StringComparison.Ordinal);
        Assert.StartsWith("明細 4 行が、あなたが開いたあとに別の人に変更されています。", JournalLineRules.LinesChangedByOthers(4), StringComparison.Ordinal);
        Assert.StartsWith("行番号 3 が 2 つの明細に付いています。", JournalLineRules.LineNoDuplicatedAt(3), StringComparison.Ordinal);
    }

    // --- 文字の欄の上限（docs/10 §4-2-1）---------------------------------------

    /// <summary>上限は docs/10 §4-2-1 の写しである。</summary>
    /// <remarks>
    /// <b>ここで値を固定しておくと、定数を書き換えた回に入口が 1 つ残る</b>
    /// （DDL とデザインとの一致は <c>FieldLengthConsistencyTests</c> が見る）。
    /// </remarks>
    [Fact]
    public void 上限は二百文字である() => Assert.Equal(200, JournalLineRules.TextMaxLength);

    /// <summary>
    /// <b>符号点で数える</b>（<c>string.Length</c> ではない）。
    /// </summary>
    /// <remarks>
    /// <b>SQLite の <c>LENGTH()</c> と同じ数え方でなければ、関門が断った値を DDL が通す</b>
    /// （docs/12 §2-2 と同じ判断）。<b>符号単位と答えが割れる字で撃つ。</b>
    /// </remarks>
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("あいう", 3)]
    [InlineData("\U0001F642", 1)]
    [InlineData("\U00020B9F", 1)]
    [InlineData("あ\U0001F642い", 3)]
    public void 符号点で数える(string? value, int expected)
        => Assert.Equal(expected, JournalLineRules.CountCharacters(value));

    /// <summary>上限に収まっていれば、そのまま返す。</summary>
    [Theory]
    [InlineData("あいうえお", 5)]
    [InlineData("あいうえお", 6)]
    [InlineData("", 1)]
    public void 収まっている文は詰めない(string value, int max)
        => Assert.Equal(value, JournalLineRules.Shorten(value, max));

    /// <summary>
    /// 超えていたら、<b>末尾に「…」を置いて上限ちょうどに詰める</b>。
    /// </summary>
    /// <remarks>
    /// <b>黙って切らない</b>（docs/21 §0）。<b>「…」も 1 文字に数える</b>ので、
    /// 詰めた結果は必ず上限ちょうどになる。
    /// </remarks>
    [Theory]
    [InlineData("あいうえお", 4, "あいう…")]
    [InlineData("あいうえお", 1, "…")]
    [InlineData("あいうえお", 2, "あ…")]
    public void 超えていたら詰めて印を置く(string value, int max, string expected)
    {
        var shortened = JournalLineRules.Shorten(value, max);

        Assert.Equal(expected, shortened);
        Assert.Equal(max, JournalLineRules.CountCharacters(shortened));
    }

    /// <summary>
    /// <b>符号点の境目で切る</b>——サロゲートペアを割らない。
    /// </summary>
    /// <remarks>
    /// <b>UTF-16 の単位で切ると、片割れだけが残って壊れた字になる。</b>
    /// </remarks>
    [Fact]
    public void サロゲートペアを割らない()
    {
        var value = string.Concat(Enumerable.Repeat("\U0001F642", 5));

        var shortened = JournalLineRules.Shorten(value, 3);

        Assert.Equal("\U0001F642\U0001F642…", shortened);
        Assert.Equal(3, JournalLineRules.CountCharacters(shortened));
    }

    /// <summary>
    /// <b>写す文は、上限を超えているときだけ詰める。</b>
    /// </summary>
    /// <remarks>
    /// <b><c>null</c> は <c>null</c> のまま</b>（「無いは NULL」。docs/20 §7）。
    /// <b>収まっている文は 1 文字も変えない</b>——写しであることが分かるのは「…」が付いたときだけである。
    /// </remarks>
    [Fact]
    public void 写す文は上限を超えているときだけ詰める()
    {
        Assert.Null(JournalLineRules.ShortenCopiedText(null));
        Assert.Equal(string.Empty, JournalLineRules.ShortenCopiedText(string.Empty));
        Assert.Equal("あいう", JournalLineRules.ShortenCopiedText("あいう"));

        var full = new string('あ', JournalLineRules.TextMaxLength);
        Assert.Equal(full, JournalLineRules.ShortenCopiedText(full));

        var over = "先頭" + new string('あ', JournalLineRules.TextMaxLength) + "末尾";
        var shortened = JournalLineRules.ShortenCopiedText(over)!;
        Assert.StartsWith("先頭", shortened, StringComparison.Ordinal);
        Assert.EndsWith("…", shortened, StringComparison.Ordinal);
        Assert.DoesNotContain("末尾", shortened, StringComparison.Ordinal);
        Assert.Equal(JournalLineRules.TextMaxLength, JournalLineRules.CountCharacters(shortened));
    }

    /// <summary>詰めた先が 1 文字未満になる呼び方は、呼び手の誤りなので止める。</summary>
    /// <remarks>
    /// <b>黙って空文字を返すと、摘要が消える。</b> 実際に渡す幅は 177 文字以上あるので、
    /// <b>ここに来るのは組み立て方を壊したときだけ</b>である。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 幅が一文字未満なら止める(int max)
        => Assert.Throws<ArgumentOutOfRangeException>(() => JournalLineRules.Shorten("あい", max));

    /// <summary><c>null</c> を詰めようとしたら止める。</summary>
    [Fact]
    public void 詰める文が_null_なら止める()
        => Assert.Throws<ArgumentNullException>(() => JournalLineRules.Shorten(null!, 10));

    /// <summary>
    /// 断りは<b>欄の名前・上限・いまの文字数・次の一手</b>を言う（docs/21 §2-6）。
    /// </summary>
    /// <remarks>
    /// <b>「計上できません」を入れない</b>——見出しが言う（`ViolationMessageTests` が見張っている）。
    /// </remarks>
    [Fact]
    public void 長すぎる摘要の断り()
        => Assert.Equal(
            "「摘要」は 200 文字以内です。いまは 231 文字あります。短くしてください。",
            JournalLineRules.DescriptionTooLong(231));

    /// <summary>明細の側は「内容」と呼ぶ（画面のラベルのとおり）。</summary>
    [Fact]
    public void 長すぎる内容の断り()
        => Assert.Equal(
            "「内容」は 200 文字以内です。いまは 201 文字あります。短くしてください。",
            JournalLineRules.ItemDescriptionTooLong(201));

    /// <summary>目に見えない字は、何文字目かまで言う。</summary>
    [Fact]
    public void 目に見えない字の断り()
        => Assert.Equal(
            "「摘要」の 3 文字目に、目に見えない文字が入っています。入力し直してください。",
            JournalLineRules.TextHasUnusableCharacter("摘要", 3));
}
