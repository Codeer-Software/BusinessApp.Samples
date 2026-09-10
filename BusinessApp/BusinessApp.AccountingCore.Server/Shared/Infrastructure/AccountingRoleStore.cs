namespace BusinessApp.AccountingCore.Server.Shared.Infrastructure;

using System.Globalization;

using BusinessApp.AccountingCore.Shared;
using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// いま操作している利用者の<b>会計の役割</b>を読む（ADR-0034）。
/// </summary>
/// <remarks>
/// <para><b>なぜ C# で読むのか。</b> 権限は原則として CLB の条件が持つが、
/// 取消・訂正の Web API（ADR-0016）は <c>IDbAccessor</c> を直に使うので
/// <b>モジュールの <c>UserWriteCondition</c> を 1 つも通らない</b>。
/// ここを閉じないと、<c>can_access_app</c> が真の利用者なら誰でも——取引先だけの利用者でも——
/// 任意の伝票に取消を計上できる。<b>取消は計上済みで不変（ADR-0004）なので取り返しがつかない</b>
/// （2026-09-02 の自己レビューで見つけた。qa/03 L-22）。</para>
/// <para><b>役割が無い・読めないときは「役割なし」に倒す。</b> 権限の判定で
/// 「分からなかったから通す」は穴になる。</para>
/// <para><b><c>app_users</c> を読むのは ADR-0032 の帰結である</b>——
/// 役割の列は認証部品の表に間借りしており（CLB の条件がそこしか見られないため）、
/// C# から読むときも同じ表を引くしかない。</para>
/// </remarks>
public sealed class AccountingRoleStore(
    IDbAccessor accessor, string dataSourceName, IAuthenticationContext authentication)
{
    /// <summary>いまの利用者の会計の役割。役割が無ければ <c>null</c>。</summary>
    public async Task<AccountingRole?> FindCurrentRoleAsync()
    {
        var userId = await authentication.GetCurrentUserIdAsync();
        if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return null;
        }

        var rows = await accessor.QueryAsync(
            dataSourceName,
            "select accounting_role from app_users where id = @p1",
            new() { { "@p1", Param(id) } });

        return rows.Count == 0 ? null : DbValue.ToDefinedEnum<AccountingRole>(rows[0]["accounting_role"]);
    }

    /// <summary>
    /// 引き渡す値の包み。
    /// </summary>
    /// <remarks>
    /// <b><c>QueryAsync</c> と <c>ExecuteAsync</c> でパラメータ辞書の型が違う</b>（qa/01 C-12）。
    /// 包み忘れても <c>new()</c> の型推論が通してしまい、実行時に Dapper が落とす。
    /// </remarks>
    private static ParamAndRawDbTypeName Param(object? value) => new() { Value = value };
}
