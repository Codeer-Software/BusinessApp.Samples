namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 会計の役割（ADR-0034）。<b>階段の段差がどこにあるか</b>を固定する。
/// </summary>
/// <remarks>
/// DB の値との往復は <c>AccountingRoleStoreTests</c>（サーバ層）が見る——
/// 変換は <c>BusinessApp.ServerSupport</c> の仕事で、純粋層はそれを知らない。
/// </remarks>
public class AccountingRoleTests
{
    /// <summary>
    /// 計上済みの伝票を動かせるのは担当と責任者だけ。
    /// </summary>
    /// <remarks>
    /// <b>3 値すべてを表明する。</b> 通る側だけを見ると「常に真」の実装でも緑になり、
    /// 帳簿閲覧に取消を許してしまう——取消は計上済みで不変（ADR-0004）なので取り返しがつかない。
    /// </remarks>
    [Theory]
    [InlineData(AccountingRole.Viewer, false)]
    [InlineData(AccountingRole.Staff, true)]
    [InlineData(AccountingRole.Manager, true)]
    public void 取消と訂正ができるのは担当と責任者だけ(AccountingRole role, bool expected)
        => Assert.Equal(expected, role.CanAmendJournals());
}
