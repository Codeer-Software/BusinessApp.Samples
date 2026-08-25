namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;

/// <summary>
/// 下書きを計上して DB に印を付ける。<b>計上する経路はここ 1 本だけにする。</b>
/// </summary>
/// <remarks>
/// <para>「検証 → 採番 → 計上済みにする」の 3 つは必ず組で起きる。呼び出し側ごとに
/// 並べ直せる形にしておくと、<b>1 か所で採番を書き戻し忘れただけで伝票番号が重複する</b>し、
/// 検証を飛ばした計上が 1 経路でも生まれれば ADR-0004 の関門がまるごと迂回される。</para>
/// <para>使うのは 2 か所。画面の保存を包む関門（<see cref="JournalSubmitGate"/>）と、
/// 「訂正する」「取り消す」でサーバが自分から計上する経路（<see cref="JournalAmendmentService"/>）。</para>
/// </remarks>
public sealed class JournalPoster(
    JournalEntryStore entryStore,
    EntryNumberSequenceStore sequenceStore,
    TimeProvider timeProvider)
{
    /// <summary>
    /// 下書きを計上する。違反があれば例外にして保存全体を巻き戻す。
    /// </summary>
    /// <param name="draft">計上する下書き。<b>DB から読み直した姿</b>を渡す（qa/01 F-12）。</param>
    /// <param name="context">計上検証に要るマスタ一式。</param>
    /// <returns>計上済みになった伝票。</returns>
    public async Task<JournalEntry> PostAsync(JournalEntry draft, PostingContext context)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Id is not { } id)
        {
            throw new InvalidOperationException("保存されていない仕訳は計上できない。");
        }

        var sequence = await sequenceStore.ReadAsync(draft.FiscalYearId);
        var result = JournalPosting.Post(draft, context, sequence, timeProvider.GetUtcNow());

        if (!result.IsPosted)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        await sequenceStore.SaveAsync(sequence, result.NextSequence!.Value);
        await entryStore.MarkPostedAsync(id, result.EntryNo!.Value, result.PostedEntry!.PostedAt!.Value);

        return result.PostedEntry;
    }
}
