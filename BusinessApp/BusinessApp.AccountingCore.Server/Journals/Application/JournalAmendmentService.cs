namespace BusinessApp.AccountingCore.Server.Journals.Application;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.ServerSupport;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using BusinessApp.AccountingCore.Server.Journals.Infrastructure;

/// <summary>
/// 伝票に対する操作（「取り消す」「訂正する」「複製する」）。docs/10 §5・ADR-0015・ADR-0048。
/// </summary>
/// <remarks>
/// <para><b>型の名前は取消・訂正の用途で付いたが、いまは複製も持つ</b>
/// （<c>JournalPostingRejectedException</c> と同じ形で、名前より持ち物が広がった）。
/// <b>複製は取消・訂正ではない</b>——帳簿を 1 行も動かさず、原仕訳の状態も見ない。
/// それでも同居させているのは、<b>権限の関門が
/// <c>JournalAmendmentEndpoint</c> の 1 メソッドにあるから</b>である
/// （この経路は CLB のモジュール条件を 1 つも通らない。qa/03 L-22）。
/// <b>入口を分けるなら、関門を先に共有部品へ括り出す</b>（2026-09-09 の自己レビュー）。</para>
/// <para>画面のボタンから Web API 経由で呼ばれる（ADR-0016）。<b>会計の判断は 1 行も
/// スクリプトに置かない</b>ので、画面がするのは「この伝票を訂正して」と頼んで、
/// 返ってきた下書きを開くことだけである（ADR-0008）。</para>
/// <para><b>取消は 1 回の操作で計上まで進む。</b> 中身をシステムが決める操作なので、
/// 下書きを見せて確認させることに意味が無い。対して訂正は、取消を計上したうえで
/// <b>再計上を下書きのまま返す</b>。中身は利用者が決めるものだからである。</para>
/// <para>途中で放棄して「取消だけ」が残るのは<b>正当な状態</b>である（ADR-0015）。
/// 単なる取消と区別が付かないが、区別する必要も無い。</para>
/// <para>呼び出し側（<c>JournalAmendmentEndpoint</c>）がトランザクションを張る。
/// <b>ここでは張らない</b>——呼び出しを組み合わせる余地を残すため（ADR-0016）。</para>
/// </remarks>
public sealed class JournalAmendmentService(
    AccountingMasterLoader masterLoader,
    JournalEntryStore entryStore,
    JournalPoster poster,
    TimeProvider timeProvider)
{
    /// <summary>本番もテストもここで組み立てる（<see cref="JournalSubmitGate.Create"/> と同じ理由）。</summary>
    public static JournalAmendmentService Create(
        IDbAccessor dbAccessor, string dataSourceName, TimeProvider timeProvider,
        IAuthenticationContext authenticationContext)
    {
        var entryStore = new JournalEntryStore(dbAccessor, dataSourceName);
        return new JournalAmendmentService(
            new AccountingMasterLoader(dbAccessor, dataSourceName),
            entryStore,
            JournalPoster.Create(dbAccessor, dataSourceName, entryStore, timeProvider, authenticationContext),
            timeProvider);
    }

    /// <summary>
    /// この伝票にできること（取消・訂正・複製）を調べる。<b>何も書かない。</b>
    /// </summary>
    /// <remarks>
    /// <para>画面がボタンを出すかどうかを決めるために使う。
    /// <b>できない操作のボタンを出さない</b>ためであって、守りではない
    /// （守りは <see cref="ReverseAsync"/> / <see cref="CorrectAsync"/> が同じ規則で行う）。</para>
    /// <para><b>可否の判断を画面に写さないための API である。</b> 種別・取消済みかどうか・
    /// 期間が開いているかを画面が自前で見ると、規則が 2 か所に分かれて片方だけ古くなる（ADR-0008）。</para>
    /// </remarks>
    public async Task<AmendmentAvailability> DescribeAsync(JournalEntryId originalId)
    {
        var original = await entryStore.FindAsync(originalId);
        if (original is null)
        {
            return AmendmentAvailability.None("対象の伝票が見つかりません。");
        }

        // **取り消された・訂正されたことは、できることの判定より先に引く。**
        // 理由の文言に混ぜると画面が拾い分けられないので別に返す（ADR-0027 §3）。
        // **「できない」で早く返る経路でも詰める**——一覧の逆引きはカレンダーと無関係に出続けるので、
        // ここで落とすと**一覧には「取り消されています」と出るのに詳細では消える**。
        // 会計期間をまだ作っていない年度で現実に起きる（2026-08-31 の自己レビュー）。
        var amendments = await entryStore.FindAmendmentEntryNosAsync(original.Id!.Value);

        var context = await masterLoader.LoadAsync();
        var today = DateOnly.FromDateTime(DatabaseTimeZone.ToWallClock(timeProvider.GetUtcNow()));

        if (context.Calendar.ResolvePeriod(today) is not AccountingPeriod period)
        {
            return AmendmentAvailability.None(
                $"今日（{today:yyyy/MM/dd}）に対応する会計期間がありません。", amendments);
        }

        var reversedOn = await entryStore.FindReversedOnAsync(original.Id!.Value);
        var reversal = JournalReversal.Reverse(
            original, today, timeProvider.GetUtcNow(),
            new ReversalContext(reversedOn is not null, period.FiscalYearId));

        // **訂正は「取消 ＋ 再計上」なので、取り消せる伝票と訂正できる伝票は今のところ同じである。**
        // 「既に訂正されている」を別に見る必要は無い——訂正があるなら必ず取消もあるので、
        // 取消の判定（既に取り消されている）で先に落ちる。
        // 2 つの値に分けてあるのは、片方だけできる状態が将来生まれうるからである。
        // **複製は原仕訳の状態を見ない**（ADR-0048 の決定 6）ので、種別だけで決まる。
        // **元にできる種別は、取消・訂正の対象にできる種別と同じ**——ドメインに「複製できるか」という
        // 属性は置かず、会計コアの名前の属性を読む（ADR-0049 の決定 6）。
        // **ここで返さないと、画面は押せるボタンを出して必ず断られる**（docs/21 §1。
        // 2026-09-09 の自己レビュー）。
        return new AmendmentAvailability(
            reversal.Created, reversal.Created, Describe(reversal),
            amendments.ReversalEntryNo, amendments.CorrectionEntryNo,
            original.EntryType.IsAmendable());
    }

    /// <summary>できない理由。<b>できるときは空</b>にして、画面が出し分けなくてよいようにする。</summary>
    private static string Describe(ReversalResult reversal)
        => string.Join(
            string.Empty,
            reversal.Violations.Where(v => v.Severity == ViolationSeverity.Error).Select(v => v.Message));

    /// <summary>
    /// 原仕訳を取り消す。反対仕訳を作って<b>計上まで進める</b>。
    /// </summary>
    /// <returns>計上した反対仕訳の識別子。</returns>
    public Task<JournalEntryId> ReverseAsync(JournalEntryId originalId)
        => WithHeadlineAsync(
            JournalPostingRejectedException.ReversalHeadline, () => ReverseCoreAsync(originalId));

    private async Task<JournalEntryId> ReverseCoreAsync(JournalEntryId originalId)
    {
        var (original, context, today, now) = await PrepareAsync(originalId);

        var reversalContext = await ResolveReversalContextAsync(original, context, today);
        var result = JournalReversal.Reverse(original, today, now, reversalContext);
        if (!result.Created)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        return await PostDraftAsync(result.Reversal!, context);
    }

    /// <summary>
    /// 原仕訳を訂正する。取消を計上し、原仕訳を写した再計上を<b>下書きのまま</b>作る。
    /// </summary>
    /// <returns>計上した取消と、これから利用者が直す再計上の下書き。</returns>
    public Task<AmendmentStarted> CorrectAsync(JournalEntryId originalId)
        => WithHeadlineAsync(
            JournalPostingRejectedException.CorrectionHeadline, () => CorrectCoreAsync(originalId));

    private async Task<AmendmentStarted> CorrectCoreAsync(JournalEntryId originalId)
    {
        var (original, context, today, now) = await PrepareAsync(originalId);

        var reversalContext = await ResolveReversalContextAsync(original, context, today);
        var result = JournalCorrection.Start(original, today, now, reversalContext);
        if (result.Drafts is not CorrectionDrafts drafts)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        var reversalId = await PostDraftAsync(drafts.Reversal, context);

        // 再計上は計上しない。中身は利用者が決める（ADR-0015）。
        var correctionId = await entryStore.InsertDraftAsync(drafts.Correction);

        return new AmendmentStarted(reversalId, correctionId);
    }

    /// <summary>
    /// 原仕訳と同じ内容の下書きを 1 本作る（ADR-0048）。
    /// </summary>
    /// <remarks>
    /// <para><b>複製は取消・訂正ではない。</b> 原仕訳の状態を何も見ないし、計上済みを 1 行も動かさない。
    /// それでも<b>同じ入口に置いてある</b>のは、<b>権限の関門を 1 か所に保つため</b>である——
    /// この経路は CLB のモジュール条件を 1 つも通らないので、
    /// 入口を分けると<b>片方に関門を書き忘れる</b>（[qa/03 L-22] がその実例）。</para>
    /// <para><b>写す欄の取捨は <see cref="JournalDuplication"/> が持つ</b>（会計補助（ステートレス）。
    /// ADR-0049）。ここがするのは、原仕訳を読むこと・今日の年度を引くこと・下書きを入れることだけである。</para>
    /// </remarks>
    /// <returns>できた下書きの識別子。画面はそれを開く。</returns>
    public Task<JournalEntryId> DuplicateAsync(JournalEntryId originalId)
        => WithHeadlineAsync(
            JournalPostingRejectedException.DuplicationHeadline, () => DuplicateCoreAsync(originalId));

    private async Task<JournalEntryId> DuplicateCoreAsync(JournalEntryId originalId)
    {
        var (original, context, today, now) = await PrepareAsync(originalId);

        // **年度は今日から引く。** 原仕訳の年度を写すと、閉じた期間へ新しい伝票を落とせる（I-04）。
        var period = TodayPeriod(context, today);
        var result = JournalDuplication.Duplicate(original, today, now, period.FiscalYearId);
        if (!result.Created)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        return await entryStore.InsertDraftAsync(result.Draft!);
    }

    /// <summary>
    /// 差し戻しの見出しを、<b>押されたボタンの言葉</b>に付け替える。
    /// </summary>
    /// <remarks>
    /// <para>取消・訂正の途中では、伝票の検証（<see cref="JournalReversal"/>）も
    /// 取消の計上（<see cref="JournalPoster"/>）も走り、どちらも既定の見出し「計上できません」で
    /// 投げてくる。しかし<b>利用者が押したのは「取り消す」か「訂正する」の 1 つ</b>で、
    /// 計上を頼んだ覚えは無い（docs/21 §2・qa/02 R24-23）。</para>
    /// <para><b>入口で 1 回包む形にしてある。</b> 投げる場所ごとに見出しを渡す形だと、
    /// 経路が 1 本増えるたびに書き漏らし、そこだけ別の言葉で断ることになる。</para>
    /// </remarks>
    private static async Task<T> WithHeadlineAsync<T>(string headline, Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (JournalPostingRejectedException rejected)
        {
            // **元の例外を内側に残す。** 包み直すと、スタックトレースが
            // ここから始まって「どの検証で落ちたか」が消える。文言と違反は同じものを渡す。
            throw new JournalPostingRejectedException(rejected.Violations, headline, rejected);
        }
    }

    /// <summary>取消・訂正のどちらでも要る材料をまとめて用意する。</summary>
    private async Task<(JournalEntry Original, AccountingMasters Context, DateOnly Today, DateTimeOffset Now)>
        PrepareAsync(JournalEntryId originalId)
    {
        // 利用者から来た識別子なので、無いことは業務のことばで返す。
        var original = await entryStore.FindAsync(originalId)
            ?? throw new JournalPostingRejectedException(
            [
                new Violation(
                    JournalViolationCodes.AmendmentTargetNotFound,
                    "対象の伝票が見つかりません。"),
            ]);

        var now = timeProvider.GetUtcNow();

        // 「今日」はプロセスのタイムゾーンではなく会計のタイムゾーンで決める。
        // UTC で動くサーバでは、日本時間の朝 8 時が前日になってしまう。
        return (original, await masterLoader.LoadAsync(), DateOnly.FromDateTime(DatabaseTimeZone.ToWallClock(now)), now);
    }

    /// <summary>
    /// 今日の属する会計期間。<b>無ければ止める</b>（取消・訂正・複製で共通）。
    /// </summary>
    /// <remarks>
    /// <b>同じ文言を 2 か所に組み立てない</b>——片方だけ直したときに、
    /// 見出しの網（<c>ViolationHeadlineTests</c>）は見出しの語を含まないので鳴らない
    /// （2026-09-09 の自己レビュー）。
    /// </remarks>
    private static AccountingPeriod TodayPeriod(AccountingMasters context, DateOnly today)
        => context.Calendar.ResolvePeriod(today) is AccountingPeriod period
            ? period
            : throw new JournalPostingRejectedException(
            [
                new Violation(
                    JournalViolationCodes.PeriodNotFound,
                    $"今日（{today:yyyy/MM/dd}）に対応する会計期間がありません。"),
            ]);

    private async Task<ReversalContext> ResolveReversalContextAsync(
        JournalEntry original, AccountingMasters context, DateOnly today)
    {
        var period = TodayPeriod(context, today);

        // 原仕訳の識別子は FindAsync が返した以上必ずある。
        var reversedOn = await entryStore.FindReversedOnAsync(original.Id!.Value);

        return new ReversalContext(reversedOn is not null, period.FiscalYearId);
    }

    /// <summary>下書きを書いて、<b>DB から読み直した姿</b>で計上する（qa/01 F-12 と同じ規律）。</summary>
    private async Task<JournalEntryId> PostDraftAsync(JournalEntry draft, AccountingMasters context)
    {
        var id = await entryStore.InsertDraftAsync(draft);
        await poster.PostAsync(await entryStore.LoadAsync(id), context);
        return id;
    }
}

/// <summary>
/// この伝票にできること。<b>画面のボタンの出し分けに使う。</b>
/// </summary>
/// <param name="CanReverse">取り消せるか。</param>
/// <param name="CanCorrect">訂正できるか。</param>
/// <param name="CanDuplicate">複製できるか。</param>
/// <param name="Reason">できない理由。できるときは空文字。</param>
/// <param name="ReversalEntryNo">既に取り消されているなら、その取消伝票の伝票番号。</param>
/// <param name="CorrectionEntryNo">既に訂正されているなら、その再計上の伝票番号。</param>
public readonly record struct AmendmentAvailability(
    bool CanReverse, bool CanCorrect, string Reason,
    int? ReversalEntryNo = null, int? CorrectionEntryNo = null, bool CanDuplicate = false)
{
    /// <summary>
    /// どちらもできない。<b>取消・訂正の番号は、分かっているなら落とさずに返す。</b>
    /// </summary>
    /// <remarks>
    /// 「取り消せるか」と「取り消されているか」は別の問いである。前者が答えられなくても
    /// 後者は答えられることがあり、<b>落とすと一覧と詳細で見えるものが食い違う</b>（ADR-0027 §3）。
    /// 対象の伝票そのものが無いときだけ、引数を省いて既定（どちらも null）にする。
    /// </remarks>
    public static AmendmentAvailability None(string reason, AmendmentEntryNumbers amendments = default)
        => new(false, false, reason, amendments.ReversalEntryNo, amendments.CorrectionEntryNo);
}

/// <summary>訂正を始めた結果。画面は再計上の下書きを開く。</summary>
/// <param name="ReversalId">計上した取消の識別子。</param>
/// <param name="CorrectionId">これから利用者が直す再計上の下書きの識別子。</param>
public readonly record struct AmendmentStarted(JournalEntryId ReversalId, JournalEntryId CorrectionId);
