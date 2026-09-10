namespace BusinessApp.AccountingCore.Server.Tests.Shared.Infrastructure;

using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.TestSupport;
using BusinessApp.AccountingCore.Server.Shared.Infrastructure;

/// <summary>
/// 操作している利用者の会計の役割を読む口（ADR-0034）。
/// </summary>
/// <remarks>
/// <b>読み違えると 2 方向に壊れる。</b> 読めないと全部差し戻され（誰も取り消せない）、
/// 甘く読むと役割の無い人が計上済みの伝票を動かせる（qa/03 L-22）。
/// </remarks>
public class AccountingRoleStoreTests
{
    private static AccountingRoleStore Store(AccountingServer server)
        => new(server.Accessor, SqliteDbAccessor.DataSourceName, server.Authentication);

    /// <summary>3 値すべてを読み戻せる（対応表が 1 つずれても緑にならないように）。</summary>
    [Theory]
    [InlineData("viewer", AccountingRole.Viewer)]
    [InlineData("staff", AccountingRole.Staff)]
    [InlineData("manager", AccountingRole.Manager)]
    public async Task 役割を読み戻せる(string stored, AccountingRole expected)
    {
        using var server = new AccountingServer();
        server.SetAccountingRole(stored);

        Assert.Equal(expected, await Store(server).FindCurrentRoleAsync());
    }

    /// <summary>役割の列が空なら「役割なし」。<b>既定を安全側に倒す。</b></summary>
    [Fact]
    public async Task 役割が空なら役割なしになる()
    {
        using var server = new AccountingServer();
        server.SetAccountingRole(null);

        Assert.Null(await Store(server).FindCurrentRoleAsync());
    }

    /// <summary>
    /// 利用者そのものが居なければ「役割なし」。
    /// </summary>
    /// <remarks>
    /// <b>「居ない」と「居るが役割が無い」を撃ち分ける。</b> 行が 0 件のときに
    /// 既定の列挙子（<c>Viewer</c>）へ落ちる実装だと、ここが通ってしまう。
    /// </remarks>
    [Fact]
    public async Task 実在しない利用者は役割なしになる()
    {
        using var server = new AccountingServer();
        server.CurrentUserId = "9999";

        Assert.Null(await Store(server).FindCurrentRoleAsync());
    }

    /// <summary>識別子が数値として読めなければ「役割なし」（qa/01 F-24 の型の入口を塞ぐ）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.5")]
    public async Task 識別子が読めなければ役割なしになる(string userId)
    {
        using var server = new AccountingServer();
        server.CurrentUserId = userId;

        Assert.Null(await Store(server).FindCurrentRoleAsync());
    }
}
