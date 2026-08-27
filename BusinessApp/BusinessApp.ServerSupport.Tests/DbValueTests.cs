namespace BusinessApp.ServerSupport.Tests;

/// <summary>
/// DB から返る <c>object?</c> の変換。
/// </summary>
/// <remarks>
/// <para><b>NULL と DBNull の両方を「無い」として扱えること</b>が肝である。
/// ADO.NET は NULL を <see cref="DBNull"/> で返すので、<c>null</c> だけを見ていると
/// 「値がある」と誤認し、そこから先の変換で落ちるか、もっと悪いことに既定値で通る。</para>
/// <para><b>列挙子は検査用のものを使う</b>（<see cref="SampleStatus"/>）。
/// もとは会計コアの <c>PeriodStatus</c> ・ <c>TaxTreatment</c> を標本にしていたが、
/// <c>DbValue</c> を共有インフラへ出した以上、その検査が会計コアを参照するのは依存の逆流である
/// （ADR-0024 §4）。<b>標本の形は変えていない</b>——1 語（<c>open</c>）と
/// 複数語（<c>for_taxable_sales</c>）の両方を、もとの文字列のまま残してある。</para>
/// </remarks>
public class DbValueTests
{
    /// <summary>
    /// 検査用の列挙子。1 語・複数語・<b>数字を含むもの</b>を持つ（snake_case の往復を見るため）。
    /// </summary>
    /// <remarks>
    /// <c>Legacy8</c> は DDL の <c>rate_kind</c> にある <c>legacy_8</c> を写したものである
    /// （<c>Designer/ddl/003_consumption_tax.sql</c>）。**まだ C# の列挙型にしていない**ので、
    /// ここが数字の前で区切ることを表明する唯一の場所になる。
    /// </remarks>
    public enum SampleStatus
    {
        Open,
        Closed,
        ForTaxableSales,
        Legacy8,
    }

    public static TheoryData<object?> Nulls => new() { null, DBNull.Value };

    [Theory]
    [MemberData(nameof(Nulls))]
    public void NULL_も_DBNull_も無いものとして扱う(object? value)
    {
        Assert.True(DbValue.IsNull(value));
        Assert.Null(DbValue.ToNullableLong(value));
        Assert.Null(DbValue.ToNullableInt(value));
        Assert.Null(DbValue.ToNullableText(value));
        Assert.Null(DbValue.ToNullableDate(value));
        Assert.Null(DbValue.ToNullableDateTimeOffset(value));
        Assert.Null(DbValue.ToNullableEnum<SampleStatus>(value));
        Assert.False(DbValue.ToBool(value));
        Assert.Equal(string.Empty, DbValue.ToText(value));
    }

    [Fact]
    public void 値があるときはそのまま変換する()
    {
        Assert.False(DbValue.IsNull(0L));
        Assert.Equal(7L, DbValue.ToLong(7L));
        Assert.Equal(7L, DbValue.ToNullableLong(7L));
        Assert.Equal(7, DbValue.ToInt(7L));
        Assert.Equal(7, DbValue.ToNullableInt(7L));
        Assert.Equal("abc", DbValue.ToText("abc"));
        Assert.Equal("abc", DbValue.ToNullableText("abc"));
    }

    [Fact]
    public void 整数のはずの列に小数が入っていたら丸めずに止める()
    {
        // **丸めてはいけない。** 金額でこれが起きると、検証は丸めた値で貸借一致と判定し、
        // DB には丸める前の値が残る（I-01 が破れる）。
        Assert.Throws<InvalidOperationException>(() => DbValue.ToLong(1000.5m));
        Assert.Throws<InvalidOperationException>(() => DbValue.ToInt(1.5m));
        Assert.Throws<InvalidOperationException>(() => DbValue.ToNullableLong(1000.5m));
        Assert.Throws<InvalidOperationException>(() => DbValue.ToNullableInt(1.5m));

        Assert.Equal(1000.5m, DbValue.ToDecimal(1000.5m));
        Assert.Equal(1000L, DbValue.ToLong(1000.0m));
    }

    [Fact]
    public void 読めない日時は黙って別の値にせず止める()
    {
        Assert.Throws<InvalidOperationException>(() => DbValue.ToDate("2026/08/24"));
        Assert.Throws<InvalidOperationException>(() => DbValue.ToDateTimeOffset("なにか"));
    }

    [Fact]
    public void 列挙子と_DB_の文字列は往復する()
    {
        Assert.Equal("open", DbValue.ToSnakeCase(SampleStatus.Open));
        Assert.Equal("for_taxable_sales", DbValue.ToSnakeCase(SampleStatus.ForTaxableSales));

        foreach (var status in Enum.GetValues<SampleStatus>())
        {
            Assert.Equal(status, DbValue.ToEnum<SampleStatus>(DbValue.ToSnakeCase(status)));
        }
    }

    /// <summary>
    /// <b>数字の前でも区切る。</b> DDL の CHECK に <c>legacy_8</c> がある。
    /// </summary>
    /// <remarks>
    /// 区切らないと <c>Legacy8</c> は <c>legacy8</c> になり、書いた瞬間に CHECK で弾かれる。
    /// **いま数字を含む列挙子は C# に 1 つも無い**ので、ここが唯一の網である
    /// （2026-08-27 の自己レビュー R16-03。写しの片方だけが規約どおりだった）。
    /// 連続する数字は 1 語として扱う（<c>Legacy80</c> → <c>legacy_80</c>）。
    /// </remarks>
    [Theory]
    [InlineData("Legacy8", "legacy_8")]
    [InlineData("Legacy80", "legacy_80")]
    [InlineData("Reduced8", "reduced_8")]
    public void 数字の前でも区切る(string name, string expected)
    {
        Assert.Equal(expected, DbValue.ToSnakeCase(name));
        Assert.Equal(name, DbValue.ToPascalCase(expected));
    }

    /// <summary>
    /// 先頭が数字でも落ちない。
    /// </summary>
    /// <remarks>
    /// <b>1 文字目には「前の文字」が無い。</b> 区切りの判定が <c>index &gt; 0</c> を先に見ずに
    /// <c>name[index - 1]</c> へ触ると、範囲外で落ちる。C# の識別子は数字で始まらないので
    /// 列挙子からは来ないが、<see cref="DbValue.ToSnakeCase(string)"/> は文字列を受ける公開の入口である。
    /// <b>境界の変異（<c>index &gt; 0</c> → <c>index &gt;= 0</c>）がミューテーションで生き残ったので足した</b>
    /// （2026-08-27。ADR-0012 §8 が「許容しない」と定めた型）。
    /// </remarks>
    [Theory]
    [InlineData("8Legacy", "8_legacy")]
    [InlineData("8", "8")]
    public void 先頭が数字でも落ちない(string name, string expected)
        => Assert.Equal(expected, DbValue.ToSnakeCase(name));

    [Fact]
    public void 名前を渡さなければ止まる()
    {
        var rejected = Assert.Throws<ArgumentNullException>(() => DbValue.ToSnakeCase(null!));
        Assert.Equal("name", rejected.ParamName);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(1L, true)]
    [InlineData(2L, true)]
    public void SQLite_の真偽値は_0_以外が真(long stored, bool expected)
        => Assert.Equal(expected, DbValue.ToBool(stored));

    [Fact]
    public void 日付は_DateTime_でも文字列でも読める()
    {
        var expected = new DateOnly(2026, 8, 24);

        Assert.Equal(expected, DbValue.ToDate(new DateTime(2026, 8, 24)));
        Assert.Equal(expected, DbValue.ToDate("2026-08-24"));
        Assert.Equal(expected, DbValue.ToDate("2026-08-24 00:00:00"));
        Assert.Equal(expected, DbValue.ToNullableDate("2026-08-24"));
    }

    /// <summary>
    /// DB に書く日付は<b>時刻まで付ける</b>。
    /// </summary>
    /// <remarks>
    /// CLB が DATE 列に書く正規形は日付＋<c>00:00:00</c> で、
    /// <b>時刻なしで書いた行だけが範囲検索から落ちる</b>（qa/01 A-04）。
    /// 書式が縮むと、書いた本人からは見えるのに帳簿の検索から消える、という壊れ方をする。
    /// </remarks>
    [Fact]
    public void DB_に書く日付は時刻まで付ける()
    {
        var date = new DateOnly(2026, 8, 24);

        Assert.Equal("2026-08-24 00:00:00", DbValue.ToDbDate(date));
        Assert.Equal(date, DbValue.ToDate(DbValue.ToDbDate(date)));
    }

    /// <summary>
    /// 日時は<b>この DB のタイムゾーン</b>（<see cref="DatabaseTimeZone"/>）で読む。
    /// </summary>
    /// <remarks>
    /// <para><b>期待値をオフセットの直値で書く。</b> もとは
    /// <c>DateTime.SpecifyKind(stored, DateTimeKind.Local)</c> を期待値にしていたが、
    /// それは<b>プロセスのタイムゾーン</b>であり、この型が避けようとしているものそのものである
    /// （<see cref="DbValue.ToDateTimeOffset"/> の注記。2026-08-27 の自己レビュー R16-02）。</para>
    /// <para><b>この機械では、直しても鳴らない。</b> 開発機が JST なので
    /// <c>SpecifyKind(..., Local)</c> と <see cref="DatabaseTimeZone"/> は観測上同じ値を返し、
    /// <b>どんな表明を書いても 2 つの実装を区別できない</b>（実際に実装を書き換えて確かめた）。
    /// それでも直す価値があるのは、<b>期待値の側がプロセスに依存しなくなる</b>からである——
    /// 古い書き方は<b>正しい実装のまま UTC の機械で落ちた</b>（期待値が +00:00 になる）。
    /// いまは UTC の機械で、正しい実装なら通り、<c>Local</c> に書き換えた実装なら落ちる。</para>
    /// </remarks>
    [Fact]
    public void 日時はプロセスではなく_DB_のタイムゾーンで読む()
    {
        var stored = new DateTime(2026, 8, 24, 13, 6, 46);
        var expected = new DateTimeOffset(2026, 8, 24, 13, 6, 46, TimeSpan.FromHours(9));

        Assert.Equal(expected, DbValue.ToDateTimeOffset(stored));
        Assert.Equal(expected, DbValue.ToDateTimeOffset("2026-08-24 13:06:46"));
        Assert.Equal(expected, DbValue.ToNullableDateTimeOffset(stored));
    }

    [Fact]
    public void 列挙子は_snake_case_から戻す()
    {
        Assert.Equal("Open", DbValue.ToPascalCase("open"));
        Assert.Equal("ForTaxableSales", DbValue.ToPascalCase("for_taxable_sales"));

        // 区切りが続いても落ちない（'a__b' のような値が紛れ込んでも例外にしない）。
        Assert.Equal("AB", DbValue.ToPascalCase("a__b"));

        Assert.Equal(SampleStatus.Open, DbValue.ToEnum<SampleStatus>("open"));
        Assert.Equal(SampleStatus.Closed, DbValue.ToNullableEnum<SampleStatus>("closed"));
    }

    [Fact]
    public void 空文字の列挙子は無いものとして扱う()
        => Assert.Null(DbValue.ToNullableEnum<SampleStatus>(string.Empty));

    [Fact]
    public void 知っている区分値だけを列挙子にする()
        => Assert.Equal(SampleStatus.Closed, DbValue.ToDefinedEnum<SampleStatus>("closed"));

    /// <summary>
    /// <b><c>Enum.TryParse</c> は数字の文字列を黙って通す。</b>
    /// <c>"0"</c> は最初の列挙子に、<c>"99"</c> は範囲外の値のまま化ける——
    /// 後者は表示名を求めた瞬間に例外になり、<b>例外にしないために作ったこの入口が 500 を生む</b>。
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("0")]
    [InlineData("99")]
    public void 知らない区分値は無いものとして扱う(string value)
        => Assert.Null(DbValue.ToDefinedEnum<SampleStatus>(value));
}
