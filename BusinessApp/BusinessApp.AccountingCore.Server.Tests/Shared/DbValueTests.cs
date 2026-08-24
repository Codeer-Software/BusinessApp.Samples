namespace BusinessApp.AccountingCore.Server.Tests.Shared;

using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Shared;

/// <summary>
/// DB から返る <c>object?</c> の変換。
/// </summary>
/// <remarks>
/// <b>NULL と DBNull の両方を「無い」として扱えること</b>が肝である。
/// ADO.NET は NULL を <see cref="DBNull"/> で返すので、<c>null</c> だけを見ていると
/// 「値がある」と誤認し、そこから先の変換で落ちるか、もっと悪いことに既定値で通る。
/// </remarks>
public class DbValueTests
{
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
        Assert.Null(DbValue.ToNullableEnum<PeriodStatus>(value));
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

    [Fact]
    public void 日時はローカル時刻として読む()
    {
        var stored = new DateTime(2026, 8, 24, 13, 6, 46);
        var expected = new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Local));

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

        Assert.Equal(PeriodStatus.Open, DbValue.ToEnum<PeriodStatus>("open"));
        Assert.Equal(PeriodStatus.Closed, DbValue.ToNullableEnum<PeriodStatus>("closed"));
    }

    [Fact]
    public void 空文字の列挙子は無いものとして扱う()
        => Assert.Null(DbValue.ToNullableEnum<PeriodStatus>(string.Empty));
}
