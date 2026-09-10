namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上済みの仕訳を訂正する（docs/10 §5・[ADR-0015]）。
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
    /// <b>取消が帳簿に無いのに再計上だけを作れる API があると、取引が帳簿に二重に載る</b>。
    /// 呼び出し側の順番に頼らず、<b>取消が帳簿に無いときは片方だけ手に入らない形</b>にしてある
    /// （サーバ側の関門の入口を 1 本にしたのと同じ理由）。
    /// 取消が既に帳簿にあるときの単独の再計上は <see cref="Resume"/>（ADR-0015 決定 4・ADR-0052）。</para>
    /// <para>取消できるかどうかの判定は <see cref="JournalReversal"/> がそのまま持つ。
    /// 訂正だけの追加規則は無い——訂正できる相手は、取り消せる相手と同じである。</para>
    /// </remarks>
    /// <param name="original">訂正する原仕訳。計上済みでなければならない。</param>
    /// <param name="postingDate">取消と再計上の計上日。訂正すると決めた日。</param>
    /// <param name="enteredAt">入力年月日。システムが決める（docs/10 §2）。</param>
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

        return new CorrectionStartResult(
            reversed.Violations,
            new CorrectionDrafts(reversed.Reversal!, Draft(original, postingDate, enteredAt, context.FiscalYearId)));
    }

    /// <summary>
    /// 訂正をやり直す。<b>取消が済んでいる原仕訳に、再計上の下書きだけを起こす</b>（ADR-0052）。
    /// </summary>
    /// <remarks>
    /// <para><see cref="Start"/> は取消と再計上を組で返し、取消が帳簿に無いのに再計上を作れない形にしてある。
    /// ここは<b>取消が既に帳簿にあるとき</b>だけ再計上を単独で起こす——ADR-0015 決定 4（誤って取り消したときの復旧は
    /// correction を単独で計上する）の道であり、訂正の下書きを消してしまった利用者の受け皿でもある（ADR-0048 の帰結）。
    /// 取消が無ければ違反で返す。取消より前の日に再計上を起こすことも <see cref="ValidateForPosting"/> と同じ規則で断る。</para>
    /// <para>再計上が既にあれば起こさない。計上済みなら 2 本目は計上の関門（<see cref="ValidateForPosting"/>）も止めるが、
    /// 下書きの段階で 2 本並ぶと利用者がどちらを直せばよいか分からなくなる。</para>
    /// </remarks>
    /// <param name="original">訂正をやり直す原仕訳。計上済みで、取り消されていなければならない。</param>
    /// <param name="postingDate">再計上の計上日。やり直すと決めた日。</param>
    /// <param name="enteredAt">入力年月日。システムが決める（docs/10 §2）。</param>
    /// <param name="context">取消・訂正の状況。呼び出し側が調べて渡す。</param>
    public static CorrectionResumeResult Resume(
        JournalEntry original,
        DateOnly postingDate,
        DateTimeOffset enteredAt,
        CorrectionResumeContext context)
    {
        ArgumentNullException.ThrowIfNull(original);

        var violations = new List<Violation>(
            AmendmentRules.ValidateOriginal(original, postingDate, AmendmentKind.Correction));

        // **取消が無いのに再計上だけを起こすと、取引が帳簿に二重に載る。** 取り消されていないなら Start の道である。
        // 計上の関門と同じ規則で見る——ここで通して計上で断る形にすると、押せるのに必ず断られるボタンになる（docs/21 §1）。
        violations.AddRange(ReversalOrder(postingDate, context.ReversedOn));

        if (context.IsAlreadyCorrected)
        {
            violations.Add(new Violation(
                JournalViolationCodes.AlreadyCorrected,
                "この伝票は既に訂正されています。やり直すときは、その訂正の伝票を訂正してください。"));
        }

        if (context.HasCorrectionDraft)
        {
            violations.Add(new Violation(
                JournalViolationCodes.CorrectionDraftExists,
                "この伝票には訂正の下書きが既にあります。振替伝票の一覧から、その下書きを開いて直してください。"));
        }

        if (violations.HasError())
        {
            return new CorrectionResumeResult(violations);
        }

        return new CorrectionResumeResult(violations, Draft(original, postingDate, enteredAt, context.FiscalYearId));
    }

    /// <summary>原仕訳を写した再計上の下書き。<see cref="Start"/> と <see cref="Resume"/> の両方がこれを使う。</summary>
    private static JournalEntry Draft(
        JournalEntry original, DateOnly postingDate, DateTimeOffset enteredAt, FiscalYearId fiscalYearId)
        => new()
        {
            // 取消と同じく、**計上日の属する会計年度**であって原仕訳の年度ではない。
            FiscalYearId = fiscalYearId,
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
        violations.AddRange(ReversalOrder(correction.PostingDate, context.ReversedOn));

        // 再計上が 2 本載れば、直した内容がそのまま二重になる。
        // やり直したいなら、その再計上を訂正する（訂正は訂正できる）。
        if (context.IsAlreadyCorrected)
        {
            violations.Add(new Violation(
                JournalViolationCodes.AlreadyCorrected,
                "この伝票は既に訂正されています。やり直すときは、その訂正の伝票を訂正してください。"));
        }

        return violations;
    }

    /// <summary>
    /// 再計上と取消の順序の規則。<b>やり直し（<see cref="Resume"/>）と計上（<see cref="ValidateForPosting"/>）で同じもの</b>を見る。
    /// </summary>
    /// <remarks>
    /// 取消が無ければ再計上は載せられない。取消より前の日に再計上が載ると、その間の期間だけ二重計上になる。
    /// 「取り消されたか」と「いつか」は <paramref name="reversedOn"/> の 1 値で受ける（<see cref="CorrectionContext"/> の注記）。
    /// </remarks>
    private static IEnumerable<Violation> ReversalOrder(DateOnly postingDate, DateOnly? reversedOn)
    {
        if (reversedOn is not DateOnly reversed)
        {
            yield return new Violation(
                JournalViolationCodes.OriginalNotReversed,
                "元の伝票がまだ取り消されていません。訂正は取消と再計上の組で行います。");
        }
        else if (postingDate < reversed)
        {
            yield return new Violation(
                JournalViolationCodes.CorrectionBeforeReversal,
                $"訂正の計上日（{postingDate:yyyy/MM/dd}）が、元の伝票を取り消した日（{reversed:yyyy/MM/dd}）より前になっています。");
        }
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
/// 訂正をやり直せるかを決めるために、伝票 1 本の外から持ってくる情報（ADR-0052）。
/// </summary>
/// <param name="ReversedOn">
/// 原仕訳を取り消した反対仕訳の計上日。<b>まだ取り消されていなければ null</b>（やり直せない。Start の道）。
/// <see cref="CorrectionContext"/> と同じく「取り消されたか」と「いつか」を 1 値で受ける。
/// </param>
/// <param name="IsAlreadyCorrected">計上済みの再計上が既にあるか。</param>
/// <param name="HasCorrectionDraft">再計上の下書きがまだあるか。</param>
/// <param name="FiscalYearId">再計上の計上日の属する会計年度。</param>
public readonly record struct CorrectionResumeContext(
    DateOnly? ReversedOn, bool IsAlreadyCorrected, bool HasCorrectionDraft, FiscalYearId FiscalYearId);

/// <summary>訂正をやり直した結果。始められたかは <see cref="Draft"/> で見る（<see cref="CorrectionStartResult"/> と同じ作法）。</summary>
/// <param name="Violations">見つかった違反（警告を含む）。</param>
/// <param name="Draft">できた再計上の下書き。始められなかったときは null。</param>
public sealed record CorrectionResumeResult(
    IReadOnlyList<Violation> Violations,
    JournalEntry? Draft = null)
{
    public bool Resumed => Draft is not null;
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
