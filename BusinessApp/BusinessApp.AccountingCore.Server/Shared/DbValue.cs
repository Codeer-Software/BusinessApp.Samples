namespace BusinessApp.AccountingCore.Server.Shared;

using System.Globalization;

/// <summary>
/// DB から返る <c>object?</c> を C# の値に直す。
/// </summary>
/// <remarks>
/// <para>ADO.NET は列の値を <c>object</c> で返し、NULL は <see cref="DBNull"/> になる。
/// この変換を各リポジトリに散らすと、片方だけ <c>DBNull</c> を見落とす、という壊れ方をする。
/// <b>DB との境界の変換はここ 1 か所に集める。</b></para>
/// <para>型付き識別子への変換（<c>long</c> → <c>AccountId</c> 等）はここではなく、
/// それぞれのリポジトリで行う（ADR-0014 の境界はリポジトリ）。</para>
/// </remarks>
internal static class DbValue
{
    public static bool IsNull(object? value) => value is null or DBNull;

    public static long ToLong(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    public static long? ToNullableLong(object? value) => IsNull(value) ? null : ToLong(value);

    public static int ToInt(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public static int? ToNullableInt(object? value) => IsNull(value) ? null : ToInt(value);

    public static string ToText(object? value) => IsNull(value) ? string.Empty : value!.ToString() ?? string.Empty;

    public static string? ToNullableText(object? value) => IsNull(value) ? null : value!.ToString();

    /// <summary>SQLite の <c>INTEGER</c> 真偽値。0 以外を true とする。</summary>
    public static bool ToBool(object? value) => !IsNull(value) && ToLong(value) != 0;

    public static DateOnly ToDate(object? value)
        => value is DateTime dt ? DateOnly.FromDateTime(dt) : DateOnly.Parse(ToText(value)[..10], CultureInfo.InvariantCulture);

    public static DateOnly? ToNullableDate(object? value) => IsNull(value) ? null : ToDate(value);

    /// <summary>
    /// 保存されている日時。<b>ローカル時刻として読む。</b>
    /// SQLite の DATETIME はオフセットを持たないので、書くときと読むときで解釈を揃える
    /// （書き込みは <c>DateTimeOffset.LocalDateTime</c>）。
    /// </summary>
    public static DateTimeOffset ToDateTimeOffset(object? value)
    {
        var dateTime = value is DateTime dt
            ? dt
            : DateTime.Parse(ToText(value), CultureInfo.InvariantCulture);
        return new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Local));
    }

    public static DateTimeOffset? ToNullableDateTimeOffset(object? value)
        => IsNull(value) ? null : ToDateTimeOffset(value);

    /// <summary>DB の snake_case を C# の列挙子名に戻す（ADR-0012 で決めた対応）。</summary>
    public static string ToPascalCase(string value)
        => string.Concat(value.Split('_')
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    public static T ToEnum<T>(object? value) where T : struct, Enum
        => Enum.Parse<T>(ToPascalCase(ToText(value)));

    public static T? ToNullableEnum<T>(object? value) where T : struct, Enum
        => IsNull(value) || ToText(value).Length == 0 ? null : ToEnum<T>(value);
}
