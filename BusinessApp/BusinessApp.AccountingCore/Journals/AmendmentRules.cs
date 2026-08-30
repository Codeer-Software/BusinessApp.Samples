namespace BusinessApp.AccountingCore.Journals;

using System.Text.RegularExpressions;
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
                "この伝票はまだ計上されていません。下書きは削除してください。");
        }

        // 原仕訳を特定できなければ、帳簿の相互関連性（規則 5 ⑤一ロ）が切れる。
        // 伝票番号は帳簿の側から原仕訳を指す手段なので、識別子と同じく無いと足せない。
        if (original.Id is null || original.EntryNo is null)
        {
            yield return new Violation(
                JournalViolationCodes.AmendmentTargetUnidentified,
                "元の伝票を特定できません（保存されていないか、伝票番号がありません）。");
        }

        // 打ち消す伝票が原仕訳より前に載ると、その間の期間の残高が原仕訳 1 本分ずれる。
        if (postingDate < original.PostingDate)
        {
            yield return new Violation(
                JournalViolationCodes.AmendmentBeforeOriginal,
                $"{kind.Noun}の計上日（{postingDate:yyyy-MM-dd}）が、元の伝票の計上日（{original.PostingDate:yyyy-MM-dd}）より前になっています。");
        }

        // **対象は通常の仕訳と訂正だけ。**
        // 取消の取消は連鎖するだけで何も表現できない（訂正をやり直したいなら訂正を訂正する）。
        // 期首残高・決算振替・繰越を打ち消すと、残高の前提（I-11・I-12）と
        // 繰越の再実行が噛み合わなくなる。
        if (!original.EntryType.IsAmendable())
        {
            yield return new Violation(
                JournalViolationCodes.AmendmentTargetNotAmendable,
                $"種別が「{original.EntryType.DisplayName()}」の伝票は対象にできません。対象にできるのは通常の伝票と訂正だけです。");
        }
    }

    /// <summary>
    /// 摘要に「何の取消・訂正か」を残す。<b>原仕訳の摘要を消さない。</b>
    /// </summary>
    /// <remarks>
    /// <para>帳簿を読む人は、足された伝票だけを見て何が起きたかを追えなければならない。</para>
    /// <para><b>接頭辞は重ねない。</b> 訂正は訂正できるので、素朴に前置きすると
    /// 「伝票番号 5 の訂正: 伝票番号 3 の訂正: 8 月分のサーバ利用料」と入れ子が伸びていく
    /// （実機で確認。2026-08-25）。摘要欄は取引の説明であって履歴ではなく、
    /// 履歴は <c>original_entry_id</c> で辿れる。<b>直前の接頭辞をすべて落として本文だけを引き継ぐ。</b></para>
    /// <para>伝票番号がある前提で書いてよい。無い伝票は <see cref="ValidateOriginal"/> が先に止めている。</para>
    /// </remarks>
    public static string Describe(JournalEntry original, AmendmentKind kind)
    {
        // **剥がすのは、原仕訳が取消・訂正のときだけ。**
        // 通常の仕訳の摘要は利用者が書いた文であって、たまたま同じ形をしていることがある
        // （「伝票番号 12 の取消について」と書いた通常の仕訳など）。
        // 無条件に剥がすと、その本文を落として計上してしまい、計上済みは二度と直せない（I-05）。
        var body = original.EntryType.RequiresOriginalEntry()
            ? StripPrefixes(original.Description)
            : (original.Description ?? string.Empty).Trim();

        return body.Length == 0
            ? $"伝票番号 {original.EntryNo} の{kind.Noun}"
            : $"伝票番号 {original.EntryNo} の{kind.Noun}: {body}";
    }

    /// <summary>
    /// 自分で付けた接頭辞（「伝票番号 N の取消: 」等）を、無くなるまで落とす。
    /// </summary>
    /// <remarks>
    /// <b>語は <see cref="AmendmentKind"/> から組み立てる。</b> ここに「取消」「訂正」と
    /// 書き写すと、文言を変えたときに剥がせなくなって入れ子が静かに復活する。
    /// </remarks>
    private static readonly Regex Prefix = new(
        $@"^伝票番号 \d+ の({AmendmentKind.Reversal.Noun}|{AmendmentKind.Correction.Noun})(: |$)",
        RegexOptions.CultureInvariant);

    private static string StripPrefixes(string? description)
    {
        // `is { Success: true }` で書くと、Match が null になり得ないぶんの分岐が
        // 到達不能なまま残る。素直に success を見る。
        var body = (description ?? string.Empty).Trim();

        for (var match = Prefix.Match(body); match.Success; match = Prefix.Match(body))
        {
            body = body[match.Length..].Trim();
        }

        return body;
    }
}

/// <summary>
/// 取消か訂正か。<b>文言のためだけに持つ</b>ので、値は表示する語そのものにしてある。
/// </summary>
/// <remarks>
/// 列挙型にして分岐すると、増えない分岐と到達しない既定値が 1 つ増えるだけになる。
/// </remarks>
/// <param name="Noun">「取消」「訂正」。</param>
/// <remarks>
/// <b>「取り消せません」「訂正できません」はここに持たない。</b> それは<b>差し戻しの見出し</b>で、
/// <c>JournalPostingRejectedException</c> が押されたボタンから決める（2026-08-31。
/// qa/02 R25-10）。両方が持つと「取り消せません。①…は取り消せません。」と 2 回言うことになる。
/// </remarks>
internal readonly record struct AmendmentKind(string Noun)
{
    public static readonly AmendmentKind Reversal = new("取消");

    public static readonly AmendmentKind Correction = new("訂正");
}
