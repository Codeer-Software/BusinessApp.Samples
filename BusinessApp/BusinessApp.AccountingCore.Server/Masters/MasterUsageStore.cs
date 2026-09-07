namespace BusinessApp.AccountingCore.Server.Masters;

using System.Globalization;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;

using static BusinessApp.AccountingCore.Server.Masters.MasterMeaningGate;

/// <summary>
/// マスタの行が<b>計上済みの仕訳明細に使われているか</b>と、いま保存されている値を読む（ADR-0038）。
/// </summary>
/// <remarks>
/// <para><b>表と列の名前は <see cref="GuardedMaster"/> / <see cref="GuardedColumn"/> でしか受けない。</b>
/// 生の文字列を受ける口を持たないので、利用者の入力が SQL に混ざる面が無い（識別子はパラメータで渡す）。</para>
/// <para><b>「使用中」は計上済みの明細があること</b>（ADR-0038 §1）。下書きだけが参照している行は
/// 数えない——下書きは直せるので、違反は計上の関門が拾う。</para>
/// </remarks>
public sealed class MasterUsageStore(IDbAccessor accessor, string dataSourceName)
{
    /// <summary>
    /// いま保存されている値（列名 → 値）。行が無ければ <c>null</c>（その保存は外部キーか楽観ロックが拒む）。
    /// </summary>
    /// <remarks>
    /// <c>SqliteDbAccessor</c> は NULL を <c>DBNull.Value</c> で返す。呼び出し側の <c>Normalize</c> は
    /// <c>null</c> と <c>DBNull</c> をどちらも空文字にするので、ここでは変換しない。
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, object?>?> FindStoredAsync(
        GuardedMaster master, long id, IReadOnlyList<GuardedColumn> columns)
    {
        var rows = await accessor.QueryAsync(
            dataSourceName,
            $"select {string.Join(", ", columns.Select(c => c.Column))} from {master.Table} where id = @p1",
            new() { { "@p1", Param(id) } });

        if (rows.Count == 0)
        {
            return null;
        }

        return columns.ToDictionary(c => c.Column, c => (object?)rows[0][c.Column]);
    }

    /// <summary>この行を参照している<b>計上済みの</b>仕訳明細の数。</summary>
    public async Task<long> CountPostedLinesAsync(GuardedMaster master, long id)
    {
        var rows = await accessor.QueryAsync(
            dataSourceName,
            $"""
            select count(*) as n
              from journal_lines l
              join journal_entries e on e.id = l.journal_entry_id
             where l.{master.LineColumn} = @p1 and e.status = 'posted'
            """,
            new() { { "@p1", Param(id) } });

        // count(*) は必ず 1 行返す
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    /// <summary>引き渡す値の包み（<c>QueryAsync</c> と <c>ExecuteAsync</c> で辞書の型が違う。qa/01 C-12）。</summary>
    private static ParamAndRawDbTypeName Param(object? value) => new() { Value = value };
}
