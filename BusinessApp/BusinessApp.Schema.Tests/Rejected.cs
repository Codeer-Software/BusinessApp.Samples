namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 書き込みが<b>狙った制約で</b>拒まれることを表明する。
/// </summary>
/// <remarks>
/// <para><b>`Assert.Throws&lt;SqliteException&gt;` だけでは足りない。</b> 落ちさえすれば緑なので、
/// <b>意図した関門とは別の関門が鳴っていても区別が付かない</b>——
/// 名前が主張していることを検査していないテストが実在した（[qa/03 の L-45](../../docs/qa/03_テストで漏らした実例.md)）。
/// 同じ形を、それを直した回に書いたばかりのテストでさらに 5 件踏んでいる（qa/02 のラウンド 88）。</para>
/// <para><b>SQLite は種類ごとに違う手掛かりを返す</b>（2026-09-13 実測）。
/// <list type="bullet">
/// <item><c>CHECK</c> は<b>式そのもの</b>を文言に出す（<c>CHECK constraint failed: status IN ('draft','posted')</c>）
/// ——どの CHECK が鳴ったかまで決まる</item>
/// <item><c>UNIQUE</c> は<b>列</b>を出す（<c>UNIQUE constraint failed: t.code</c>）。
/// <b>索引の名前は出ないので、同じ列に付いた列制約と一意索引は見分けられない</b></item>
/// <item>外部キーは <c>FOREIGN KEY constraint failed</c> だけで、<b>どの外部キーかは分からない</b>。
/// 種類の区別は拡張エラーコードで行う</item>
/// <item>トリガは <c>RAISE(ABORT, …)</c> に書いた文言がそのまま返る</item>
/// </list></para>
/// <para><b>拡張エラーコードは種類を分ける</b>（<c>SqliteExtendedErrorCode</c>）。
/// 「CHECK のつもりが NOT NULL で落ちていた」のような取り違えを、文言より確実に止める。</para>
/// </remarks>
internal static class Rejected
{
    private const int Check = 275;
    private const int ForeignKey = 787;
    private const int NotNull = 1299;
    private const int PrimaryKey = 1555;
    private const int Trigger = 1811;
    private const int Unique = 2067;

    /// <summary><c>CHECK</c> で拒まれること。<paramref name="expression"/> は CHECK の式の一部。</summary>
    public static void ByCheck(SqliteConnection db, string sql, string expression)
        => Because(db, sql, Check, expression);

    /// <summary>
    /// <c>UNIQUE</c> で拒まれること。<paramref name="columns"/> は <c>表.列</c>（複合なら <c>, </c> 区切り）。
    /// </summary>
    public static void ByUnique(SqliteConnection db, string sql, string columns)
        => Because(db, sql, Unique, columns);

    /// <summary>外部キーで拒まれること。<b>どの外部キーかは SQLite が教えないので、種類までしか見られない。</b></summary>
    public static void ByForeignKey(SqliteConnection db, string sql)
        => Because(db, sql, ForeignKey, expected: null);

    /// <summary>トリガで拒まれること。<paramref name="message"/> は <c>RAISE(ABORT, …)</c> の文言の一部。</summary>
    /// <param name="label">
    /// 検体の呼び名。<b>落ちたときの説明に出る</b>——`[Theory]` の値だけだと、
    /// 何を撃ったつもりだったのかが読み取れないことがある（`'2026-04-01' || char(0) || 'zzz'` など）。
    /// </param>
    public static void ByTrigger(SqliteConnection db, string sql, string message, string? label = null)
        => Because(db, sql, Trigger, message, label);

    /// <summary><c>NOT NULL</c> で拒まれること。</summary>
    public static void ByNotNull(SqliteConnection db, string sql, string column)
        => Because(db, sql, NotNull, column);

    /// <summary>主キーで拒まれること。</summary>
    public static void ByPrimaryKey(SqliteConnection db, string sql, string columns)
        => Because(db, sql, PrimaryKey, columns);

    private static void Because(
        SqliteConnection db, string sql, int extendedErrorCode, string? expected, string? label = null)
    {
        var what = label is null ? string.Empty : $"（{label}）";
        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));

        Assert.True(
            thrown.SqliteExtendedErrorCode == extendedErrorCode,
            $"{extendedErrorCode} で拒まれるはずが {thrown.SqliteExtendedErrorCode} だった{what}: {thrown.Message}");

        if (expected is not null)
        {
            Assert.True(
                thrown.Message.Contains(expected, StringComparison.Ordinal),
                $"断りの文言に「{expected}」が無い{what}: {thrown.Message}");
        }
    }
}
