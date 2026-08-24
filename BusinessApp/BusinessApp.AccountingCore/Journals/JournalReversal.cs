namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上済みの仕訳を取り消す反対仕訳を作る（docs/04 §5）。
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
    /// <param name="enteredAt">入力年月日。システムが決める（docs/04 §2）。</param>
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
            // 取引そのものを説明する欄であって、訂正作業の日ではないからである（docs/04 §5）。
            TransactionDate = original.TransactionDate,
            PostingDate = postingDate,
            Status = EntryStatus.Draft,
            EntryType = EntryType.Reversal,
            OriginalEntryId = original.Id,
            Description = Describe(original),
            PartnerId = original.PartnerId,
            EnteredAt = enteredAt,
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
                "この仕訳は既に取り消されている。");
        }

        // 下書きは帳簿ではないので、取り消すのではなく消せばよい（docs/04 §5）。
        // 下書きに反対仕訳を立てられると、帳簿に「取り消された何か」が増えるだけになる。
        if (original.Status != EntryStatus.Posted)
        {
            yield return new Violation(
                JournalViolationCodes.ReversalTargetNotPosted,
                "計上していない仕訳は取り消せない。下書きはそのまま削除する。");
        }

        // 原仕訳を特定できなければ、帳簿の相互関連性（規則 5 ⑤一ロ）が切れる。
        // 伝票番号は帳簿の側から原仕訳を指す手段なので、識別子と同じく無いと取り消せない。
        if (original.Id is null || original.EntryNo is null)
        {
            yield return new Violation(
                JournalViolationCodes.ReversalTargetUnidentified,
                "原仕訳を特定できないので取り消せない（保存されていないか、伝票番号が無い）。");
        }

        // 取消は取消であって、新しい取引ではない。原仕訳より前に計上できない。
        if (postingDate < original.PostingDate)
        {
            yield return new Violation(
                JournalViolationCodes.ReversalBeforeOriginal,
                $"取消の計上日 {postingDate:yyyy-MM-dd} が、原仕訳の計上日 {original.PostingDate:yyyy-MM-dd} より前になっている。");
        }

        // **取り消してよいのは通常の仕訳だけ。**
        // 取消の取消は連鎖するだけで何も表現できない。期首残高・決算振替・繰越を
        // 反対仕訳で打ち消すと、残高の前提（I-11・I-12）と繰越の再実行が噛み合わなくなる。
        if (original.EntryType != EntryType.Normal)
        {
            yield return new Violation(
                JournalViolationCodes.ReversalTargetNotNormal,
                $"種別が「{original.EntryType}」の仕訳は取り消せない。取り消せるのは通常の仕訳だけ。");
        }
    }

    /// <summary>明細の貸借を入れ替える。金額・科目・部門・税区分はそのまま写す。</summary>
    private static JournalLine Reverse(JournalLine line)
        => line with { DebitCredit = line.DebitCredit.Opposite() };

    /// <summary>
    /// 摘要に「何の取消か」を残す。<b>原仕訳の摘要を消さない。</b>
    /// 帳簿を読む人は、取消だけを見て何が起きたかを追えなければならない。
    /// </summary>
    /// <remarks>
    /// 伝票番号がある前提で書いてよい。無い伝票は <see cref="Validate"/> が先に止めている。
    /// </remarks>
    private static string Describe(JournalEntry original)
        => string.IsNullOrWhiteSpace(original.Description)
            ? $"伝票番号 {original.EntryNo} の取消"
            : $"伝票番号 {original.EntryNo} の取消: {original.Description}";
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
