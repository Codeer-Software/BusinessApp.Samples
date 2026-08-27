namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.ServerSupport;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 伝票番号の採番表（<c>journal_entry_sequences</c>）の読み書き。
/// </summary>
/// <remarks>
/// <b>進めるだけで戻さない</b>（I-17）。読み書きは計上と同じトランザクションの中で行う。
/// 更新は <c>WHERE next_entry_no = 読んだ値</c> を付けて、同時計上で同じ番号が二度出ないようにする。
/// <para><b>引数の型が Query と Execute で違う。</b> <c>QueryAsync</c> は
/// <c>Dictionary&lt;string, ParamAndRawDbTypeName&gt;</c>、<c>ExecuteAsync</c> は
/// <c>Dictionary&lt;string, object&gt;</c>（生値）を取る。後者に包んだ値を渡すと
/// <c>new()</c> の型推論で素通りし、実行時に Dapper が落とす（qa/01 C-12）。</para>
/// </remarks>
public sealed class EntryNumberSequenceStore(IDbAccessor dbAccessor, string dataSourceName)
{
    /// <summary>会計年度の採番を読む。行が無ければ 1 番から始める。</summary>
    public async Task<EntryNumberSequence> ReadAsync(FiscalYearId fiscalYearId)
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            "select next_entry_no from journal_entry_sequences where fiscal_year_id = @p1",
            new() { { "@p1", new ParamAndRawDbTypeName { Value = fiscalYearId.Value } } });

        var row = rows.FirstOrDefault();
        return row is null
            ? EntryNumberSequence.StartOf(fiscalYearId)
            : new EntryNumberSequence(fiscalYearId, DbValue.ToInt(row["next_entry_no"]));
    }

    /// <summary>進めた採番を書き戻す。読んだ値から変わっていたら書き込まない（同時計上の検出）。</summary>
    public async Task SaveAsync(EntryNumberSequence previous, EntryNumberSequence next)
    {
        // 引数は同じ型が 2 つ並ぶので、取り違えてもコンパイルは通る。
        // 取り違えると UPDATE も INSERT も 0 行になり、「競合した」という**嘘の理由**で落ちる。
        if (previous.FiscalYearId != next.FiscalYearId || next.NextValue != previous.NextValue + 1)
        {
            throw new ArgumentException(
                $"採番の進め方が壊れている（{previous} → {next}）。読んだ値と、それを 1 つ進めた値の順で渡す。",
                nameof(next));
        }

        var affected = await dbAccessor.ExecuteAsync(
            dataSourceName,
            """
            update journal_entry_sequences set next_entry_no = @p2
            where fiscal_year_id = @p1 and next_entry_no = @p3
            """,
            new()
            {
                { "@p1", next.FiscalYearId.Value },
                { "@p2", next.NextValue },
                { "@p3", previous.NextValue },
            });

        if (affected == 1)
        {
            return;
        }

        // 行がまだ無い会計年度なら作る。あるのに更新できなかったなら、他の計上が先に番号を取っている。
        var inserted = await dbAccessor.ExecuteAsync(
            dataSourceName,
            """
            insert into journal_entry_sequences (fiscal_year_id, next_entry_no)
            select @p1, @p2
            where not exists (select 1 from journal_entry_sequences where fiscal_year_id = @p1)
            """,
            new()
            {
                { "@p1", next.FiscalYearId.Value },
                { "@p2", next.NextValue },
            });

        if (inserted != 1)
        {
            throw new InvalidOperationException(
                "伝票番号の採番が競合した。もう一度計上してください。");
        }
    }
}
