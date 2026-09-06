namespace BusinessApp.ServerSupport;

/// <summary>
/// 帳簿の日時を解釈するタイムゾーン。
/// </summary>
/// <remarks>
/// <para><b>プロセスのタイムゾーンに依存させない。</b> 入力年月日は
/// 「通常の業務処理期間の経過後に入力した事実を確認できる」という優良な電子帳簿の要件
/// （規則 5 ⑤一イ(2)）そのものなので、サーバを UTC で動かしただけで日付が前日にずれる、
/// という壊れ方をしてはいけない。SQLite の DATETIME はオフセットを持たないため、
/// <b>書くときと読むときの解釈をここ 1 か所で揃える。</b></para>
/// <para><b>今は日本標準時で固定している。</b> 対象ペルソナは国内の法人だけで
/// （<c>docs/02_ペルソナ.md</c>）、日本の会計・税務のための部品だからである。
/// 事業所情報から取るべきかは保留（<c>docs/04</c> §5）。</para>
/// <para>Windows と Linux で ID が違うので両方を試す。見つからなければ落とす。
/// <b>黙って UTC に落ちる実装にしない</b>（それが起きたら日付が 9 時間ずれた帳簿ができる）。</para>
/// </remarks>
public static class DatabaseTimeZone
{
    /// <summary>Windows と Linux で ID が違うので、通る方を使う。</summary>
    private static readonly string[] CandidateIds = ["Asia/Tokyo", "Tokyo Standard Time"];

    public static TimeZoneInfo Value { get; } = Resolve(CandidateIds);

    /// <summary>オフセットを持たない「壁時計の時刻」を、会計のタイムゾーンの時点として解釈する。</summary>
    public static DateTimeOffset FromWallClock(DateTime wallClock)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, Value.GetUtcOffset(unspecified));
    }

    /// <summary>時点を、会計のタイムゾーンの「壁時計の時刻」に直す（DB に書く形）。</summary>
    public static DateTime ToWallClock(DateTimeOffset instant)
        => TimeZoneInfo.ConvertTime(instant, Value).DateTime;

    /// <summary>
    /// 候補を順に試す。<b>見つからなければ落とす。</b>
    /// 黙って UTC に落ちる実装にすると、日付が 9 時間ずれた帳簿が静かにできあがる。
    /// </summary>
    internal static TimeZoneInfo Resolve(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
            {
                return found;
            }
        }

        throw new InvalidOperationException(
            "日本標準時のタイムゾーンが見つからない。会計コアは日時の解釈をこれに揃えている。");
    }
}
