namespace BusinessApp.AccountingCore.Server.Masters.Infrastructure;

using System.Globalization;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;


/// <summary>
/// マスタの値を保存する前に、DB へ問い合わせるもの（<c>MasterSubmitGate</c> の口）。
/// </summary>
/// <remarks>
/// <para><b>利用者が入れた値は、必ずパラメータで渡す</b>（<see cref="MasterUsageStore"/> と同じ作法）。
/// SQL の文へ埋め込むのは<b>表と列の名前だけ</b>で、その出どころは
/// <see cref="CodedMaster"/> か<b>呼び出し側がソースに書いたリテラル</b>
/// （<see cref="FindStoredAsync"/> の <c>columns</c>）に限られる。
/// <b><c>columns</c> に外から来た文字列を渡さないこと</b>——
/// 型は防いでいないので、ここだけは書く人が守る（2026-09-09 の自己レビュー）。</para>
/// <para><b>大小を無視した突き合わせは、DB の <c>COLLATE NOCASE</c> に任せる。</b>
/// C# 側で畳むと、DB の一意索引と畳み方がずれたときに気づけない——
/// <b>同じ照合順序で同じことを 2 回言わない</b>（docs/20 §4）。</para>
/// </remarks>
public sealed class MasterCodeStore(IDbAccessor accessor, string dataSourceName)
{
    /// <summary>
    /// 同じコードの行が既にあれば、<b>その行に保存されている字</b>を返す（<b>大小を無視して探す</b>。自分自身は除く）。
    /// </summary>
    /// <remarks>
    /// <b>補助科目だけは勘定科目ごとに数える</b>——一意なのは（勘定科目, コード）の組だからである。
    /// </remarks>
    /// <param name="master">守るマスタ。</param>
    /// <param name="code">保存しようとしているコード（正規化済み）。</param>
    /// <param name="id">更新なら自分の識別子。新規なら <c>null</c>。</param>
    /// <param name="parentId">補助科目の勘定科目。他のマスタでは <c>null</c>。</param>
    public async Task<string?> FindConflictingCodeAsync(CodedMaster master, string code, long? id, long? parentId)
    {
        ArgumentNullException.ThrowIfNull(master);

        var scope = master.Parent is null ? string.Empty : $" and {master.Parent.Column} = @p3";
        var rows = await accessor.QueryAsync(
            dataSourceName,
            $"select code from {master.Table}"
            + " where code = @p1 collate nocase and (@p2 is null or id <> @p2)" + scope + " limit 1",
            new()
            {
                { "@p1", Param(code) },
                { "@p2", Param(id) },
                { "@p3", Param(parentId) },
            });

        // **ぶつかった相手の字をそのまま返す。** 大小だけが違うとき、
        // 利用者は自分が入れた字と見比べないと理由が分からない。
        return rows.Count == 0 ? null : Convert.ToString(rows[0]["code"], CultureInfo.InvariantCulture);
    }

    /// <summary>「全社共通」の部門が既にあるか（自分自身は除く）。</summary>
    /// <remarks>
    /// <b>DDL の部分 UNIQUE インデックスと同じことを見る。</b> 二層に置く狙いは、
    /// DB に当たると定型文になる失敗を、利用者の語で先に断ることである（qa/03 L-28）。
    /// </remarks>
    public async Task<bool> CompanyWideDepartmentExistsAsync(long? id)
    {
        var rows = await accessor.QueryAsync(
            dataSourceName,
            "select count(*) as n from departments"
            + " where is_company_wide = 1 and (@p1 is null or id <> @p1)",
            new() { { "@p1", Param(id) } });

        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>
    /// いま保存されている値（列名 → 値）。行が無ければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>触っていない欄の値が要る</b>ので引く。CLB は変更されたフィールドしか送らないため
    /// （qa/01 F-12）、税区分の整合のように<b>2 つの欄をまたぐ規則</b>は、
    /// 片方だけ送られてきたときに保存されている側と組んで判定する。
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, object?>?> FindStoredAsync(
        CodedMaster master, long id, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(columns);

        var rows = await accessor.QueryAsync(
            dataSourceName,
            $"select {string.Join(", ", columns)} from {master.Table} where id = @p1",
            new() { { "@p1", Param(id) } });

        return rows.Count == 0 ? null : columns.ToDictionary(c => c, c => (object?)rows[0][c]);
    }

    /// <summary>その勘定科目が補助科目を使うか。科目が無ければ <c>null</c>。</summary>
    /// <remarks>
    /// <b>使わない科目の下に補助科目を作れてしまう穴</b>を塞ぐための問い合わせである
    /// （[ADR-0038 §3](../../../docs/decisions/0038-使用中のマスタは意味を変えられない.md) の 2 値は、
    /// 2026-09-08 の回では明細の側しか塞いでいなかった。docs/04 §1 の B-1）。
    /// </remarks>
    public async Task<bool?> UsesSubAccountAsync(long accountId)
    {
        var rows = await accessor.QueryAsync(
            dataSourceName,
            "select uses_sub_account from accounts where id = @p1",
            new() { { "@p1", Param(accountId) } });

        return rows.Count == 0
            ? null
            : Convert.ToInt64(rows[0]["uses_sub_account"], CultureInfo.InvariantCulture) == 1;
    }

    /// <summary>引き渡す値の包み（<c>QueryAsync</c> と <c>ExecuteAsync</c> で辞書の型が違う。qa/01 C-12）。</summary>
    private static ParamAndRawDbTypeName Param(object? value) => new() { Value = value };
}
