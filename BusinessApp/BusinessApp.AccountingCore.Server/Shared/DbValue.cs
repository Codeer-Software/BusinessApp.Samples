namespace BusinessApp.AccountingCore.Server.Shared;

using System.Globalization;

/// <summary>
/// DB から返る <c>object?</c> を C# の値に直す。
/// </summary>
/// <remarks>
/// <para>ADO.NET は列の値を <c>object</c> で返し、NULL は <see cref="DBNull"/> になる。
/// この変換を各リポジトリに散らすと、片方だけ <c>DBNull</c> を見落とす、という壊れ方をする。
/// <b>DB との境界の変換はここ 1 か所に集める。例外を作らない。</b></para>
/// <para><b>丸めない。</b> <c>Convert.ToInt64</c> は小数を黙って丸めるので、整数のはずの列に
/// 小数が入っていたことに気づけない。金額でこれが起きると、検証は丸めた値で貸借一致と判定し、
/// DB には丸める前の値が残る（I-01 が破れる）。整数として読む列は
/// <see cref="ToDecimal"/> で受けてから整数性を確かめる。</para>
/// <para>型付き識別子への変換（<c>long</c> → <c>AccountId</c> 等）はここではなく、
/// それぞれのリポジトリで行う（ADR-0014 の境界はリポジトリ）。</para>
/// </remarks>
internal static class DbValue
{
    public static bool IsNull(object? value) => IsNullCore(value);

    private static bool IsNullCore(object? value) => value is null or DBNull;

    /// <summary>数値をそのまま <see cref="decimal"/> で受ける。<b>丸めない。</b></summary>
    public static decimal ToDecimal(object? value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    public static long ToLong(object? value) => (long)Integral(value);

    public static long? ToNullableLong(object? value) => IsNullCore(value) ? null : ToLong(value);

    public static int ToInt(object? value) => (int)Integral(value);

    public static int? ToNullableInt(object? value) => IsNullCore(value) ? null : ToInt(value);

    /// <summary>整数のはずの列を読む。小数が入っていたら黙って丸めずに止める。</summary>
    private static decimal Integral(object? value)
    {
        var number = ToDecimal(value);
        return number == decimal.Truncate(number)
            ? number
            : throw new InvalidOperationException($"整数のはずの列に小数が入っている: {number}");
    }

    public static string? ToNullableText(object? value) => IsNullCore(value) ? null : value!.ToString();

    /// <summary>無い値を空文字にする。「無い」と「空」を区別したいときは <see cref="ToNullableText"/>。</summary>
    public static string ToText(object? value) => ToNullableText(value) ?? string.Empty;

    /// <summary>SQLite の <c>INTEGER</c> 真偽値。0 以外を true とする。</summary>
    public static bool ToBool(object? value) => !IsNullCore(value) && ToDecimal(value) != 0m;

    /// <summary>
    /// 保存されている日付。<b>形式を決め打ちする。</b>
    /// 曖昧な解釈を許すと、想定外の書式が入ったときに黙って別の日付になる。
    /// </summary>
    public static DateOnly ToDate(object? value)
        => value is DateTime dt
            ? DateOnly.FromDateTime(dt)
            : DateOnly.FromDateTime(ParseDateTime(ToText(value)));

    public static DateOnly? ToNullableDate(object? value) => IsNullCore(value) ? null : ToDate(value);

    /// <summary>
    /// 保存されている日時。<b>会計のタイムゾーン</b>（<see cref="AccountingTimeZone"/>）で解釈する。
    /// SQLite の DATETIME はオフセットを持たないので、書くときと読むときで解釈を揃える。
    /// <b>プロセスのタイムゾーンに依存させない</b>（サーバを UTC で動かすと入力年月日が前日になる）。
    /// </summary>
    public static DateTimeOffset ToDateTimeOffset(object? value)
    {
        var dateTime = value is DateTime dt ? dt : ParseDateTime(ToText(value));
        return AccountingTimeZone.FromWallClock(dateTime);
    }

    public static DateTimeOffset? ToNullableDateTimeOffset(object? value)
        => IsNullCore(value) ? null : ToDateTimeOffset(value);

    /// <summary>SQLite が返す文字列日時。秒未満の桁数が可変なので、書式を並べて受ける。</summary>
    private static readonly string[] DateTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd",
    ];

    private static DateTime ParseDateTime(string text)
        => DateTime.TryParseExact(text, DateTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"日時として読めない値が入っている: {text}");

    /// <summary>DB の snake_case を C# の列挙子名に戻す（ADR-0012 で決めた対応）。</summary>
    public static string ToPascalCase(string value)
        => string.Concat(value.Split('_')
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    /// <summary>
    /// C# の列挙子を DB の snake_case に直す。<see cref="ToPascalCase"/> の逆。
    /// <b>列挙子に対応する文字列を手で書かない</b>ための変換であり、
    /// 手書きの定数は列挙子を変えたときに黙って一致しなくなる。
    /// </summary>
    public static string ToSnakeCase<T>(T value) where T : struct, Enum
    {
        var name = value.ToString()!;
        var text = new System.Text.StringBuilder(name.Length + 4);

        foreach (var (character, index) in name.Select((c, i) => (c, i)))
        {
            if (char.IsUpper(character) && index > 0)
            {
                text.Append('_');
            }

            text.Append(char.ToLowerInvariant(character));
        }

        return text.ToString();
    }

    public static T ToEnum<T>(object? value) where T : struct, Enum
        => Enum.Parse<T>(ToPascalCase(ToText(value)));

    public static T? ToNullableEnum<T>(object? value) where T : struct, Enum
        => IsNullCore(value) || ToText(value).Length == 0 ? null : ToEnum<T>(value);
}
