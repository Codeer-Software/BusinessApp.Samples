namespace BusinessApp.AccountingCore.Server.Masters.Infrastructure;

using System.Globalization;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;


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

    /// <summary>
    /// この行が<b>計上済みで使われている数</b>。0 なら意味を変えてよい。
    /// </summary>
    /// <remarks>
    /// <para><b>数える単位はマスタで違う</b>（<see cref="GuardedMaster.UsageUnit"/>）。
    /// 会計コアの 4 マスタは<b>仕訳明細の行</b>を数える——明細でしか選べないからである。</para>
    /// <para><b>取引先だけは振替伝票の枚数を数える。</b> 取引先は伝票にも明細にも入り、
    /// <b>明細が空なら伝票の値が実効値になる</b>（docs/10 §6-2）。
    /// 明細だけを数えると「伝票にだけ取引先を入れた計上済みの伝票」を取りこぼし、
    /// 行を数えると同じ伝票を何度も数えて「3 行で使われています」と言ってしまう。</para>
    /// </remarks>
    public async Task<long> CountPostedLinesAsync(GuardedMaster master, long id)
    {
        ArgumentNullException.ThrowIfNull(master);

        var sql = master.EntryColumn is null
            ? $"""
              select count(*) as n
                from journal_lines l
                join journal_entries e on e.id = l.journal_entry_id
               where l.{master.LineColumn} = @p1 and e.status = 'posted'
              """
            : $"""
              select count(*) as n
                from journal_entries e
               where e.status = 'posted'
                 and (e.{master.EntryColumn} = @p1
                      or exists (select 1 from journal_lines l
                                  where l.journal_entry_id = e.id and l.{master.LineColumn} = @p1))
              """;

        var rows = await accessor.QueryAsync(dataSourceName, sql, new() { { "@p1", Param(id) } });

        // count(*) は必ず 1 行返す
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture);
    }

    /// <summary>引き渡す値の包み（<c>QueryAsync</c> と <c>ExecuteAsync</c> で辞書の型が違う。qa/01 C-12）。</summary>
    private static ParamAndRawDbTypeName Param(object? value) => new() { Value = value };
}
