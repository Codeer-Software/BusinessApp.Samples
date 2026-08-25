namespace BusinessApp.AccountingCore.Server.Tests.Shared;

using BusinessApp.AccountingCore.Server.Shared;
using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;

/// <summary>
/// 1 つの操作をトランザクションで包む（<see cref="DbTransactionScope"/>）。
/// </summary>
/// <remarks>
/// <b>会計コアの安全性はここに全面的に乗っている</b>（ADR-0004）。
/// 以前はこの 6 行がコントローラとテストのフィクスチャに書き写されていて、
/// <b>コントローラ側の <c>RollbackAsync</c> を <c>CommitAsync</c> に変えても全テストが緑だった</b>
/// （qa/02 R4-04）。写しを 1 か所に寄せたので、ここが唯一の実装である。
/// </remarks>
public class DbTransactionScopeTests : IDisposable
{
    private readonly SqliteConnection connection = TestDatabase.CreateWithSeed();
    private readonly SqliteDbAccessor accessor;

    public DbTransactionScopeTests() => accessor = new SqliteDbAccessor(connection);

    public void Dispose()
    {
        connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task 成功したら確定する()
    {
        var affected = await DbTransactionScope.RunAsync(accessor, () => InsertPartnerAsync("P901"));

        Assert.Equal(1, affected);
        Assert.Equal(1, CountPartners("P901"));
    }

    /// <summary>
    /// 例外が出たら<b>丸ごと巻き戻し、例外はそのまま投げ直す</b>。
    /// </summary>
    /// <remarks>
    /// 握りつぶすと、呼び出し側は「成功した」と見なして先へ進む。
    /// 訂正なら<b>取消だけが残った状態を成功として返す</b>ことになる。
    /// </remarks>
    [Fact]
    public async Task 例外が出たら巻き戻して投げ直す()
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DbTransactionScope.RunAsync<int>(accessor, async () =>
            {
                await InsertPartnerAsync("P902");
                throw new InvalidOperationException("途中で落とす");
            }));

        Assert.Equal("途中で落とす", thrown.Message);
        Assert.Equal(0, CountPartners("P902"));
    }

    /// <summary>
    /// 続けて何度でも使える（＝トランザクションを閉じ忘れていない）。
    /// </summary>
    /// <remarks>
    /// 閉じ忘れると次の呼び出しが「トランザクションが二重に始まっている」で落ちる。
    /// <b>失敗した直後も使える</b>ことまで見る——巻き戻しの経路で閉じ忘れるほうが起きやすい。
    /// </remarks>
    [Fact]
    public async Task 失敗した直後でも続けて使える()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DbTransactionScope.RunAsync<int>(accessor, () => throw new InvalidOperationException("1 回目")));

        await DbTransactionScope.RunAsync(accessor, () => InsertPartnerAsync("P903"));

        Assert.Equal(1, CountPartners("P903"));
    }

    [Fact]
    public async Task 引数の欠落は受け付けない()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DbTransactionScope.RunAsync(null!, () => Task.FromResult(0)));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DbTransactionScope.RunAsync<int>(accessor, null!));
    }

    private Task<int> InsertPartnerAsync(string code)
        => accessor.ExecuteAsync(
            SqliteDbAccessor.DataSourceName,
            "insert into partners (code, name) values (@p1, @p2)",
            new() { { "@p1", code }, { "@p2", "検査用" } });

    private int CountPartners(string code)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from partners where code = '{code}'";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
