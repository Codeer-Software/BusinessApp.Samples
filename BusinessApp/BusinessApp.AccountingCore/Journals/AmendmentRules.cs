namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 取消（反対仕訳）と訂正（再計上）に共通する、<b>原仕訳の側</b>の規則（docs/04 §5）。
/// </summary>
/// <remarks>
/// <para>取消と訂正は「計上済みの伝票に、それを打ち消す／直す伝票を足す」という同じ形をしている。
/// 原仕訳に求めることも同じなので、<b>2 か所に書かない</b>。片方だけ緩めたことに
/// 気づけないのが一番危ない（ADR-0004 の迂回路はいつもそうやってできる）。</para>
/// <para>操作ごとに違うのは「既に取り消されているか」「既に訂正されているか」だけで、
/// それらは <see cref="JournalReversal"/> と <see cref="JournalCorrection"/> が各自で見る。</para>
/// </remarks>
internal static class AmendmentRules
{
    /// <summary>原仕訳に取消・訂正を足せるかを見る。足す側の中身は見ない。</summary>
    /// <param name="original">対象の原仕訳。</param>
    /// <param name="postingDate">足す伝票の計上日。</param>
    /// <param name="kind">取消か訂正か。文言に使う。</param>
    public static IEnumerable<Violation> ValidateOriginal(
        JournalEntry original, DateOnly postingDate, AmendmentKind kind)
    {
        // 下書きは帳簿ではないので、打ち消すのではなく消せばよい（docs/04 §5）。
        // 下書きに反対仕訳を立てられると、帳簿に「取り消された何か」が増えるだけになる。
        if (original.Status != EntryStatus.Posted)
        {
            yield return new Violation(
                JournalViolationCodes.AmendmentTargetNotPosted,
                $"計上していない仕訳は{kind.CannotVerb}。下書きはそのまま削除する。");
        }

        // 原仕訳を特定できなければ、帳簿の相互関連性（規則 5 ⑤一ロ）が切れる。
        // 伝票番号は帳簿の側から原仕訳を指す手段なので、識別子と同じく無いと足せない。
        if (original.Id is null || original.EntryNo is null)
        {
            yield return new Violation(
                JournalViolationCodes.AmendmentTargetUnidentified,
                $"原仕訳を特定できないので{kind.CannotVerb}（保存されていないか、伝票番号が無い）。");
        }

        // 打ち消す伝票が原仕訳より前に載ると、その間の期間の残高が原仕訳 1 本分ずれる。
        if (postingDate < original.PostingDate)
        {
            yield return new Violation(
                JournalViolationCodes.AmendmentBeforeOriginal,
                $"{kind.Noun}の計上日 {postingDate:yyyy-MM-dd} が、"
                + $"原仕訳の計上日 {original.PostingDate:yyyy-MM-dd} より前になっている。");
        }

        // **対象は通常の仕訳と訂正だけ。**
        // 取消の取消は連鎖するだけで何も表現できない（訂正をやり直したいなら訂正を訂正する）。
        // 期首残高・決算振替・繰越を打ち消すと、残高の前提（I-11・I-12）と
        // 繰越の再実行が噛み合わなくなる。
        if (!original.EntryType.IsAmendable())
        {
            yield return new Violation(
                JournalViolationCodes.AmendmentTargetNotAmendable,
                $"種別が「{original.EntryType}」の仕訳は{kind.CannotVerb}。"
                + "対象にできるのは通常の仕訳と訂正だけ。");
        }
    }
}

/// <summary>
/// 取消か訂正か。<b>文言のためだけに持つ</b>ので、値は表示する語そのものにしてある。
/// </summary>
/// <remarks>
/// 列挙型にして分岐すると、増えない分岐と到達しない既定値が 1 つ増えるだけになる。
/// </remarks>
/// <param name="Noun">「取消」「訂正」。</param>
/// <param name="CannotVerb">「取り消せない」「訂正できない」。</param>
internal readonly record struct AmendmentKind(string Noun, string CannotVerb)
{
    public static readonly AmendmentKind Reversal = new("取消", "取り消せない");

    public static readonly AmendmentKind Correction = new("訂正", "訂正できない");
}
