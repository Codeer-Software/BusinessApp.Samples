namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上済みの仕訳を訂正する（docs/04 §5・[ADR-0015]）。
/// </summary>
/// <remarks>
/// <para>訂正は<b>取消 1 本 ＋ 正しい内容の再計上 1 本</b>で表す。計上済みは書き換えないので、
/// 帳簿には 3 本（原仕訳・取消・再計上）が残る。どちらの伝票も
/// <c>OriginalEntryId</c> で<b>原仕訳を</b>指すだけで、2 本を結ぶ列は持たない（ADR-0015）。</para>
/// <para><b>取消と違い、再計上の中身は利用者が決める。</b> だからここが作るのは
/// 「原仕訳を写しただけの下書き」であって、計上時にサーバが上書きすることはない。
/// 代わりに計上時は <see cref="ValidateForPosting"/> で<b>関係</b>だけを見る。</para>
/// <para>純粋関数である。DB も時計も知らない（ADR-0008）。</para>
/// </remarks>
public static class JournalCorrection
{
    /// <summary>
    /// 訂正を始める。<b>取消の下書きと再計上の下書きを同時に返す。</b>
    /// </summary>
    /// <remarks>
    /// <para>2 つに分けない理由。再計上の下書きは「原仕訳が取り消された」ことを前提にしており、
    /// <b>取消を作らずに再計上だけを作れる API があると、取引が帳簿に二重に載る</b>。
    /// 呼び出し側の順番に頼らず、<b>片方だけ手に入らない形</b>にしてある
    /// （サーバ側の関門の入口を 1 本にしたのと同じ理由）。</para>
    /// <para>取消できるかどうかの判定は <see cref="JournalReversal"/> がそのまま持つ。
    /// 訂正だけの追加規則は無い——訂正できる相手は、取り消せる相手と同じである。</para>
    /// </remarks>
    /// <param name="original">訂正する原仕訳。計上済みでなければならない。</param>
    /// <param name="postingDate">取消と再計上の計上日。訂正すると決めた日。</param>
    /// <param name="enteredAt">入力年月日。システムが決める（docs/04 §2）。</param>
    /// <param name="context">取消の可否を決めるために伝票の外から持ってくる情報。</param>
    public static CorrectionStartResult Start(
        JournalEntry original,
        DateOnly postingDate,
        DateTimeOffset enteredAt,
        ReversalContext context)
    {
        // null の検査は JournalReversal.Reverse が持つ。ここで二重に置かない。
        var reversed = JournalReversal.Reverse(original, postingDate, enteredAt, context);
        if (!reversed.Created)
        {
            return new CorrectionStartResult(reversed.Violations);
        }

        var correction = new JournalEntry
        {
            // 取消と同じく、**計上日の属する会計年度**であって原仕訳の年度ではない。
            FiscalYearId = context.FiscalYearId,
            // 取引日は原仕訳と同じで始める。**ただし利用者が直せる**——
            // 「取引日を打ち間違えた」こと自体が訂正の理由になりうるからである。
            TransactionDate = original.TransactionDate,
            PostingDate = postingDate,
            Status = EntryStatus.Draft,
            EntryType = EntryType.Correction,
            OriginalEntryId = original.Id,
            Description = AmendmentRules.Describe(original, AmendmentKind.Correction),
            PartnerId = original.PartnerId,
            EnteredAt = enteredAt,
            // 原仕訳をそのまま写す。**正しい姿ではなく、直す前の姿を出す。**
            // 利用者は誤っている箇所だけを直せばよく、打ち直しにならない。
            Lines = [.. original.Lines],
            // **投入元の情報は写す。** 一意なのは冪等キーだけで（I-14）、それだけを落とせばよい。
            // 落としてしまうと、投入元の部品は自分が投げた伝票の訂正を帳簿から辿れなくなる。
            SourceComponent = original.SourceComponent,
            SourceDocumentId = original.SourceDocumentId,
        };

        return new CorrectionStartResult(
            reversed.Violations, new CorrectionDrafts(reversed.Reversal!, correction));
    }

    /// <summary>
    /// 再計上を計上してよいかを検査する。<b>中身は見ない</b>（中身は利用者が決めるもので、
    /// 貸借一致や期間は <see cref="JournalEntryValidator"/> が通常の仕訳と同じように見る）。
    /// </summary>
    /// <param name="correction">計上しようとしている再計上の下書き。</param>
    /// <param name="original">その原仕訳。</param>
    /// <param name="context">原仕訳の取消・訂正の状況。</param>
    public static IReadOnlyList<Violation> ValidateForPosting(
        JournalEntry correction, JournalEntry original, CorrectionContext context)
    {
        ArgumentNullException.ThrowIfNull(correction);
        ArgumentNullException.ThrowIfNull(original);

        var violations = new List<Violation>(
            AmendmentRules.ValidateOriginal(original, correction.PostingDate, AmendmentKind.Correction));

        // **ここが訂正の要である。** 原仕訳が生きたまま再計上を足すと、
        // 帳簿には「原仕訳」と「直した内容」の両方が載り、取引が二重に計上される。
        // 取消を先に立てることを、規約ではなく検証で強制する（ADR-0004）。
        if (context.ReversedOn is not { } reversedOn)
        {
            violations.Add(new Violation(
                JournalViolationCodes.OriginalNotReversed,
                "原仕訳がまだ取り消されていない。訂正は、取消と再計上の組で行う。"));
        }
        else if (correction.PostingDate < reversedOn)
        {
            // 取消より前に再計上が載ると、その間の期間だけ二重計上になる。
            violations.Add(new Violation(
                JournalViolationCodes.CorrectionBeforeReversal,
                $"再計上の計上日 {correction.PostingDate:yyyy-MM-dd} が、"
                + $"原仕訳を取り消した日 {reversedOn:yyyy-MM-dd} より前になっている。"));
        }

        // 再計上が 2 本載れば、直した内容がそのまま二重になる。
        // やり直したいなら、その再計上を訂正する（訂正は訂正できる）。
        if (context.IsAlreadyCorrected)
        {
            violations.Add(new Violation(
                JournalViolationCodes.AlreadyCorrected,
                "この仕訳は既に訂正されている。やり直すなら、その訂正の伝票を訂正する。"));
        }

        return violations;
    }

}

/// <summary>
/// 再計上を計上できるかを決めるために、伝票 1 本の外から持ってくる情報。
/// </summary>
/// <remarks>
/// <see cref="ReversalContext"/> と同じ考え方で、<b>ドメインは DB を知らない</b>ので
/// 呼び出し側が調べて渡す（ADR-0008）。
/// </remarks>
/// <param name="ReversedOn">
/// 原仕訳を取り消した反対仕訳の計上日。<b>まだ取り消されていなければ null。</b>
/// 「取り消されたか」と「いつ取り消されたか」を 2 つの引数にすると、
/// 片方だけ渡して矛盾した状態を作れてしまうので 1 つにしてある。
/// </param>
/// <param name="IsAlreadyCorrected">この原仕訳に対する計上済みの再計上が既にあるか。</param>
public readonly record struct CorrectionContext(DateOnly? ReversedOn, bool IsAlreadyCorrected);

/// <summary>
/// 訂正を始めた結果。
/// </summary>
/// <remarks>
/// <b>違反が空かどうかで判定しない。</b> 警告は返るが訂正は始められるので、
/// 始められたかは <see cref="Drafts"/> で見る（<see cref="ReversalResult"/> と同じ作法）。
/// </remarks>
/// <param name="Violations">見つかった違反（警告を含む）。</param>
/// <param name="Drafts">できた 2 本の下書き。始められなかったときは null。</param>
public sealed record CorrectionStartResult(
    IReadOnlyList<Violation> Violations,
    CorrectionDrafts? Drafts = null)
{
    public bool Started => Drafts is not null;
}

/// <summary>
/// 訂正でできる 2 本の下書き。<b>片方だけを取り出せない形にしてある。</b>
/// </summary>
/// <remarks>
/// 「取消と再計上は組である」を 2 つの null 許容プロパティで表すと、
/// 片方だけ入った状態が型の上では作れてしまい、<b>取消を伴わない再計上</b>
/// ——つまり二重計上——を表現できることになる。組を 1 つの値にして、そこを塞ぐ。
/// </remarks>
/// <param name="Reversal">原仕訳を打ち消す取消の下書き。</param>
/// <param name="Correction">原仕訳を写した再計上の下書き。</param>
public readonly record struct CorrectionDrafts(JournalEntry Reversal, JournalEntry Correction);
