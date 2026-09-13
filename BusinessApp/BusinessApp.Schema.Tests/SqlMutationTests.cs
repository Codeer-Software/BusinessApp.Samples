namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

/// <summary>
/// <b>SQL ミューテーションの注入そのものを検体で固定する</b>（ADR-0056）。
/// </summary>
/// <remarks>
/// <para><b>注入が届いていないのに緑を返すのが、この道具で最も危ない壊れ方である</b>——
/// 掃引はそれを<b>「テストが見張っている」と数えてしまう</b>（ADR-0053 決定 2 が避けた形と同じ）。
/// だから<b>「当たらなかった」ではなく「投げる」</b>ようにしてあり、それをここで固定する。</para>
/// <para><b>判定はディスクを見ない。</b> <see cref="TestDatabase.Mutate"/> は
/// 文字列を受け取って文字列を返すので、検体で全部の枝を撃てる。</para>
/// </remarks>
[Collection(QuerySqlCollection.Name)]
public class SqlMutationTests
{
    private const string Sql = "SELECT a FROM t WHERE x >= @p ORDER BY b DESC";

    private static string Spec(string text, string replacement, string? found = null)
    {
        var start = Sql.IndexOf(text, StringComparison.Ordinal);

        return $"Book:{start}:{text.Length}:{replacement}:{found ?? text}";
    }

    [Fact]
    public void 指した位置だけを置き換える()
        => Assert.Equal(
            "SELECT a FROM t WHERE x > @p ORDER BY b DESC",
            TestDatabase.Mutate(Sql, "Book", Spec(">=", ">")));

    /// <summary><b>置換後は空でよい</b>（<c>DISTINCT</c> の削除など）。</summary>
    [Fact]
    public void 置換後が空なら取り除く()
        => Assert.Equal(
            "SELECT a FROM t WHERE x >= @p ORDER BY b",
            TestDatabase.Mutate(Sql, "Book", Spec(" DESC", string.Empty)));

    /// <summary>
    /// <b>名指されたモジュール以外は素通しする。</b>
    /// </summary>
    /// <remarks>
    /// 1 回の掃引で流すテストが 2 つ以上のモジュールに触ることがあり、
    /// <b>全部に当てると「どれが殺したか」が分からなくなる</b>。
    /// </remarks>
    [Fact]
    public void 別のモジュールには当てない()
        => Assert.Equal(Sql, TestDatabase.Mutate(Sql, "Ledger", "Book:0:6:DELETE:SELECT"));

    /// <summary>
    /// <b>指した位置に思っていた字が無ければ投げる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>位置だけでは足りない。</b> 数える側と読む側が<b>同じ文字列を見ている保証は無い</b>
    /// ——BOM が 1 つ付いただけ、改行が <c>CRLF</c> になっただけで<b>全部の位置がずれる</b>。
    /// ずれた置換は<b>構文として通ることがあり、結果が変わって赤くなり、
    /// 掃引はそれを「殺した」と数える</b>——**最良の報告が返る**、最も危ない壊れ方である。</para>
    /// <para><b>SQL を直したあと掃引を流し直していない</b>ときも、同じようにここで落ちる。</para>
    /// </remarks>
    [Fact]
    public void 指した位置に思っていた字が無ければ投げる()
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => TestDatabase.Mutate(Sql, "Book", Spec(">=", ">", found: "<=")));

        Assert.Contains("別の場所を指している", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>形が壊れていたら黙って素通ししない。</b>
    /// </summary>
    /// <remarks>
    /// <b>素通しすると、注入が届いていないのに緑が返る</b>——
    /// 掃引はそれを「殺した」と数え、<b>生き残りの数が実際より少なく見える</b>。
    /// </remarks>
    [Theory]
    [InlineData("Book:0:6:X")]            // 欄が足りない（原文が無い）
    [InlineData("Book:0:6")]              // さらに足りない
    [InlineData("Book:あ:6:X:SELECT")]    // 位置が数でない
    [InlineData("Book:0:い:X:SELECT")]    // 長さが数でない
    [InlineData("Book:-1:6:X:SELECT")]    // 負の位置
    [InlineData("")]                      // 空
    public void 形が壊れていたら投げる(string mutation)
        => Assert.Throws<ArgumentException>(() => TestDatabase.Mutate(Sql, "Book", mutation));

    /// <summary>
    /// <b>SQL の外を指していたら投げる。</b> <b>桁が溢れる大きさでも投げる。</b>
    /// </summary>
    /// <remarks>
    /// <c>start + length</c> で比べると、大きな <c>start</c> で<b>負に折り返して検査を素通りする</b>。
    /// </remarks>
    [Theory]
    [InlineData(40, 10)]
    [InlineData(2147483647, 1)]
    public void 範囲の外を指していたら投げる(int start, int length)
    {
        var thrown = Assert.Throws<ArgumentException>(
            () => TestDatabase.Mutate(Sql, "Book", $"Book:{start}:{length}:X:SELECT"));

        Assert.Contains("外を指している", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>環境変数が立っていれば、読み込んだ SQL が本当に変わる。</b>
    /// </summary>
    /// <remarks>
    /// <b>ここが配線の唯一の突き合わせである</b>——<see cref="TestDatabase.Mutate"/> の検体は
    /// 純関数を固定するだけで、<b>掃引が立てた環境変数が行動テストの読む SQL まで届くか</b>は言っていない。
    /// <b>届かなければ、掃引は全点を「生き残り」と報告する</b>（安全側だが、計器としては死んでいる）。
    /// </remarks>
    [Fact]
    public void 環境変数を立てると読み込んだSQLが変わる()
    {
        var original = TestDatabase.QuerySql("PartnerRegistrationList");
        var start = original.IndexOf(">=", StringComparison.Ordinal);
        Assert.True(start >= 0, "検体にする '>=' が登録一覧の SQL に無い。");

        try
        {
            Environment.SetEnvironmentVariable(
                TestDatabase.SqlMutationVariable, $"PartnerRegistrationList:{start}:2:>:>=");

            var mutated = TestDatabase.QuerySql("PartnerRegistrationList");

            Assert.NotEqual(original, mutated);
            Assert.Equal(original.Length - 1, mutated.Length);
            Assert.Equal('>', mutated[start]);
        }
        finally
        {
            // **必ず消す。** 残すと、同じプロセスの後続のテストが壊れた SQL で走る。
            Environment.SetEnvironmentVariable(TestDatabase.SqlMutationVariable, null);
        }

        Assert.Equal(original, TestDatabase.QuerySql("PartnerRegistrationList"));
    }

    /// <summary>
    /// <b>別のモジュールの SQL には、立っていても当たらない。</b>
    /// </summary>
    [Fact]
    public void 環境変数が別のモジュールを指していれば原本のまま()
    {
        var original = TestDatabase.QuerySql("JournalBook");

        try
        {
            Environment.SetEnvironmentVariable(
                TestDatabase.SqlMutationVariable, "PartnerRegistrationList:0:6:DELETE:SELECT");

            Assert.Equal(original, TestDatabase.QuerySql("JournalBook"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestDatabase.SqlMutationVariable, null);
        }
    }
}
