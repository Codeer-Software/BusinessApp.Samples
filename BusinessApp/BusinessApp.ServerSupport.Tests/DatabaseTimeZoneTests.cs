namespace BusinessApp.ServerSupport.Tests;


/// <summary>
/// 帳簿の日時を解釈するタイムゾーン。
/// </summary>
/// <remarks>
/// <b>プロセスのタイムゾーンに依存しないこと</b>が要点である。サーバを UTC で動かしただけで
/// 入力年月日が前日になる、という壊れ方をしてはいけない（規則 5 ⑤一イ(2)）。
/// </remarks>
public class DatabaseTimeZoneTests
{
    [Fact]
    public void 壁時計の時刻を日本標準時の時点として読む()
    {
        var instant = DatabaseTimeZone.FromWallClock(new DateTime(2026, 8, 24, 13, 6, 46));

        Assert.Equal(new DateTimeOffset(2026, 8, 24, 13, 6, 46, TimeSpan.FromHours(9)), instant);
    }

    [Fact]
    public void 時点を日本標準時の壁時計に直す()
    {
        // UTC で 04:06 は JST で 13:06。DB に書くのは後者。
        var instant = new DateTimeOffset(2026, 8, 24, 4, 6, 46, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 8, 24, 13, 6, 46), DatabaseTimeZone.ToWallClock(instant));
    }

    [Fact]
    public void 書いて読むと同じ時点に戻る()
    {
        var instant = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.FromHours(9));

        Assert.Equal(instant, DatabaseTimeZone.FromWallClock(DatabaseTimeZone.ToWallClock(instant)));
    }

    [Fact]
    public void 候補は順に試す()
        => Assert.Equal(DatabaseTimeZone.Value, DatabaseTimeZone.Resolve(["存在しない", "Asia/Tokyo"]));

    [Fact]
    public void 見つからなければ落とす_黙って_UTC_にしない()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => DatabaseTimeZone.Resolve(["存在しない"]));

        Assert.Contains("日本標準時", error.Message, StringComparison.Ordinal);
    }
}
