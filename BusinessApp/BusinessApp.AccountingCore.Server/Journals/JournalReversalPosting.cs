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
    public async Task<JournalEntry> ApplyAsync(JournalEntry draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Id is not { } id)
        {
            throw new InvalidOperationException("保存されていない取消には書き込めない。");
        }

        if (draft.OriginalEntryId is not { } originalId)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(JournalViolationCodes.OriginalEntryMissing, "取消には、取り消す原仕訳が要る。"),
            ]);
        }

        var original = await entryStore.LoadAsync(originalId);
        var context = new ReversalContext(await entryStore.HasReversalAsync(originalId));
        var result = JournalReversal.Reverse(original, draft.PostingDate, draft.EnteredAt, context);

        if (!result.Created)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        await entryStore.ReplaceLinesAsync(id, result.Reversal!.Lines);
        await entryStore.UpdateDescriptionAsync(id, result.Reversal.Description);

        // 書き戻した姿を読み直す。**検証にかけるのは DB に入っている内容**である。
        return await entryStore.LoadAsync(id);
    }
}
