namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 取消の中身をシステムが決める（docs/04 §5）。
/// </summary>
/// <remarks>
/// <para><b>送られてきた明細は使わない。</b> 取消は「原仕訳の貸借を入れ替えたもの」と決まっていて、
/// 利用者が中身を決める余地は無い。画面から何が来ても、原仕訳から作り直したもので上書きする。</para>
/// <para>関門（<see cref="JournalSubmitGate"/>）から分けてあるのは、
/// 「保存を包む」ことと「取消の中身を決める」ことが別の仕事だからである。</para>
/// </remarks>
public sealed class JournalReversalPosting(JournalEntryStore entryStore)
{
    /// <summary>
    /// 取消の下書きに、原仕訳を反転した明細と摘要を書き込み、書き戻した姿を返す。
    /// 取り消せないときは例外にして保存全体を巻き戻す。
    /// </summary>
    public async Task<JournalEntry> ApplyAsync(JournalEntry draft, PostingContext context)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);

        // **書く前に状態を見る。** 計上済みに書き込もうとするとトリガが生の SQLite 例外を出し、
        // 業務のことばで差し戻せなくなる。
        if (draft.Status != EntryStatus.Draft)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(JournalViolationCodes.AlreadyPosted, "計上済みの伝票は、もう一度計上できません。"),
            ]);
        }

        if (draft.Id is not { } id)
        {
            throw new InvalidOperationException("保存されていない取消には書き込めない。");
        }

        if (draft.OriginalEntryId is not { } originalId)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(JournalViolationCodes.OriginalEntryMissing, "取消には、取り消す元の伝票の指定が必要です。"),
            ]);
        }

        // 会計年度は**取消の計上日**から引く。原仕訳の年度を写すと、年度をまたぐ取消が
        // 「作れたのに計上できない」という一番読みにくい行き止まりになる。
        if (context.Calendar.ResolvePeriod(draft.PostingDate) is not { } period)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(
                    JournalViolationCodes.PeriodNotFound,
                    $"計上日（{draft.PostingDate:yyyy-MM-dd}）に対応する会計期間がありません。"),
            ]);
        }

        var original = await entryStore.LoadAsync(originalId);
        var reversalContext = new ReversalContext(
            await entryStore.FindReversedOnAsync(originalId) is not null, period.FiscalYearId);
        var result = JournalReversal.Reverse(original, draft.PostingDate, draft.EnteredAt, reversalContext);

        if (!result.Created)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        await entryStore.ReplaceLinesAsync(id, result.Reversal!.Lines);
        await entryStore.OverwriteReversalHeaderAsync(id, result.Reversal);

        // 書き戻した姿を読み直す。**検証にかけるのは DB に入っている内容**である。
        return await entryStore.LoadAsync(id);
    }
}
