namespace BusinessApp.AccountingCore.Server.Journals.Application;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Server.Journals.Infrastructure;

/// <summary>
/// 再計上（訂正）の計上を通してよいかを、原仕訳との<b>関係</b>で判定する（ADR-0015）。
/// </summary>
/// <remarks>
/// <para><see cref="JournalReversalPosting"/> と対になるが、やることは逆である。
/// 取消は<b>中身をサーバが上書きする</b>のに対し、<b>再計上の中身は利用者が決める</b>ので
/// 一切書き換えない。代わりに「原仕訳が取り消されているか」「既に訂正されていないか」を見る。</para>
/// <para><b>ここが訂正の最後の関門である。</b> 画面は「訂正する」ボタンからしか
/// 再計上を作らせないが、新規作成で種別「訂正」を選んで原仕訳を指すこともできる。
/// 原仕訳が生きたまま再計上が通ると、取引が帳簿に二重に載る。</para>
/// </remarks>
public sealed class JournalCorrectionPosting(JournalEntryStore entryStore)
{
    /// <summary>
    /// 再計上の下書きを検証する。通れば渡された下書きをそのまま返す。
    /// 通らなければ例外にして保存全体を巻き戻す。
    /// </summary>
    public async Task<JournalEntry> ApplyAsync(JournalEntry draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        // 計上済みをもう一度通そうとしたら、業務のことばで先に止める。
        // 通してもトリガが生の SQLite 例外を出すだけで、利用者に何も伝わらない。
        if (draft.Status != EntryStatus.Draft)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(JournalViolationCodes.AlreadyPosted, "この伝票は計上済みです。画面を開き直してください。"),
            ]);
        }

        if (draft.OriginalEntryId is not JournalEntryId originalId)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(JournalViolationCodes.OriginalEntryMissing, "訂正には、訂正する元の伝票の指定が必要です。"),
            ]);
        }

        var original = await entryStore.LoadAsync(originalId);
        var context = new CorrectionContext(
            await entryStore.FindReversedOnAsync(originalId),
            await entryStore.HasCorrectionAsync(originalId));

        var violations = JournalCorrection.ValidateForPosting(draft, original, context);
        if (violations.HasError())
        {
            throw new JournalPostingRejectedException(violations);
        }

        // 貸借一致・期間・部門・科目は、通常の仕訳とまったく同じ検証が見る。
        // ここで見直すと規則が 2 か所に散り、片方だけ緩んでも気づけない。
        return draft;
    }
}
