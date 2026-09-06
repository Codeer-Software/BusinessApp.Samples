namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上済みの仕訳を取り消す反対仕訳を作る（docs/10 §5）。
/// </summary>
/// <remarks>
/// <para>計上済みの仕訳は変更も削除もしない。訂正・取消は<b>反対仕訳を 1 本足す</b>ことで表す
/// （[ADR-0004]、通達 8-9）。原仕訳は帳簿に残り続ける。</para>
/// <para><b>総額方式を採る。</b> 貸借を入れ替えて同額を立てる。純額方式（同一科目のマイナス金額）も
/// 法令上は認められるが（一問一答 問26）、総額方式が最も読みやすく、集計時の符号の扱いが単純になる。</para>
/// <para>純粋関数である。DB も時計も知らず、計上日と入力年月日は呼び出し側が渡す（ADR-0008）。
/// <b>ここで作るのは下書きである。</b> 計上は <see cref="JournalPosting"/> を通す。
/// 取消だからといって検証を省く経路を作らない。</para>
/// </remarks>
public static class JournalReversal
{
    /// <summary>
    /// 取消の反対仕訳（下書き）を作る。取り消せない伝票のときは違反を返す。
    /// </summary>
    /// <param name="original">取り消す原仕訳。計上済みでなければならない。</param>
    /// <param name="postingDate">反対仕訳の計上日。取り消すと決めた日。</param>
    /// <param name="enteredAt">入力年月日。システムが決める（docs/10 §2）。</param>
    /// <param name="context">
    /// 伝票 1 本だけでは決まらないこと。呼び出し側が調べて渡す。
    /// <b>省略可能にしない。</b> 既定値は「まだ取り消されていない」＝最も危険な側になる。
    /// </param>
    public static ReversalResult Reverse(
        JournalEntry original,
        DateOnly postingDate,
        DateTimeOffset enteredAt,
        ReversalContext context)
    {
        ArgumentNullException.ThrowIfNull(original);

        var violations = Validate(original, postingDate, context).ToList();
        if (violations.HasError())
        {
            return new ReversalResult(violations);
        }

        var reversal = new JournalEntry
        {
            // **計上日の属する会計年度**であって、原仕訳の年度ではない。
            // 3 月の仕訳を 4 月に取り消せば、反対仕訳は新しい年度に載る。
            FiscalYearId = context.FiscalYearId,
            // **取引日は原仕訳と同じにする**（訂正に気づいた日ではない）。帳簿の「取引年月日」は
            // 取引そのものを説明する欄であって、訂正作業の日ではないからである（docs/10 §5）。
            TransactionDate = original.TransactionDate,
            PostingDate = postingDate,
            Status = EntryStatus.Draft,
            EntryType = EntryType.Reversal,
            OriginalEntryId = original.Id,
            Description = AmendmentRules.Describe(original, AmendmentKind.Reversal),
            PartnerId = original.PartnerId,
            EnteredAt = enteredAt,
            // **投入元の情報は写す。** 一意なのは冪等キーだけで（I-14）、それだけを落とせばよい。
            SourceComponent = original.SourceComponent,
            SourceDocumentId = original.SourceDocumentId,
            Lines = [.. original.Lines.Select(Reverse)],
        };

        return new ReversalResult(violations, reversal);
    }

    private static IEnumerable<Violation> Validate(
        JournalEntry original, DateOnly postingDate, ReversalContext context)
    {
        // 二重取消は残高を狂わせる。取り消したものをもう一度取り消しても、
        // 帳簿には「同じ金額の反対仕訳が 2 本」が残るだけで、元の取引は 1 回しか無い。
        if (context.IsAlreadyReversed)
        {
            yield return new Violation(
                JournalViolationCodes.AlreadyReversed,
                "この伝票は既に取り消されています。");
        }

        // 原仕訳の側に求めることは訂正と同じなので、規則は 1 か所にまとめてある。
        foreach (var violation in AmendmentRules.ValidateOriginal(original, postingDate, AmendmentKind.Reversal))
        {
            yield return violation;
        }
    }

    /// <summary>明細の貸借を入れ替える。金額・科目・部門・税区分はそのまま写す。</summary>
    private static JournalLine Reverse(JournalLine line)
        => line with { DebitCredit = line.DebitCredit.Opposite() };

}

/// <summary>
/// 取消の可否を決めるために、伝票 1 本の外から持ってくる情報。
/// </summary>
/// <remarks>
/// <see cref="PostingContext"/> と同じ考え方で、<b>ドメインは DB を知らない</b>ので
/// 呼び出し側が調べて渡す（ADR-0008）。
/// </remarks>
/// <param name="IsAlreadyReversed">この原仕訳を取り消す計上済みの反対仕訳が既にあるか。</param>
/// <param name="FiscalYearId">
/// 取消の計上日が属する会計年度。<b>原仕訳の年度ではない。</b>
/// 年度をまたいで取り消すと、反対仕訳は新しい年度に載って新しい番号を採る。
/// </param>
public readonly record struct ReversalContext(bool IsAlreadyReversed, FiscalYearId FiscalYearId);

/// <summary>
/// 反対仕訳を作った結果。
/// </summary>
/// <remarks>
/// <b>違反が空かどうかで判定しない。</b> 警告は返るが取消はできるので、
/// できたかどうかは <see cref="Created"/> で見る（<see cref="PostingResult"/> と同じ作法）。
/// </remarks>
/// <param name="Violations">見つかった違反（警告を含む）。</param>
/// <param name="Reversal">作れたときの反対仕訳（下書き）。作れなかったときは null。</param>
public sealed record ReversalResult(IReadOnlyList<Violation> Violations, JournalEntry? Reversal = null)
{
    public bool Created => Reversal is not null;
}
