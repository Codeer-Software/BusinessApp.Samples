namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Shared;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 計上済みの伝票を「取り消す」「訂正する」（docs/04 §5・ADR-0015）。
/// </summary>
/// <remarks>
/// <para>画面のボタンから Web API 経由で呼ばれる（ADR-0016）。<b>会計の判断は 1 行も
/// スクリプトに置かない</b>ので、画面がするのは「この伝票を訂正して」と頼んで、
/// 返ってきた下書きを開くことだけである（ADR-0008）。</para>
/// <para><b>取消は 1 回の操作で計上まで進む。</b> 中身をシステムが決める操作なので、
/// 下書きを見せて確認させることに意味が無い。対して訂正は、取消を計上したうえで
/// <b>再計上を下書きのまま返す</b>。中身は利用者が決めるものだからである。</para>
/// <para>途中で放棄して「取消だけ」が残るのは<b>正当な状態</b>である（ADR-0015）。
/// 単なる取消と区別が付かないが、区別する必要も無い。</para>
/// <para>呼び出し側（<see cref="JournalAmendmentEndpoint"/>）がトランザクションを張る。
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
    /// この伝票を取り消せるか・訂正できるかを調べる。<b>何も書かない。</b>
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
            return new AmendmentAvailability(false, false, "対象の伝票が見つかりません。");
        }

        var context = await masterLoader.LoadAsync();
        var today = DateOnly.FromDateTime(AccountingTimeZone.ToWallClock(timeProvider.GetUtcNow()));

        if (context.Calendar.ResolvePeriod(today) is not AccountingPeriod period)
        {
            return new AmendmentAvailability(
                false, false, $"今日（{today:yyyy-MM-dd}）に対応する会計期間がありません。");
        }

        var reversedOn = await entryStore.FindReversedOnAsync(original.Id!.Value);
        var reversal = JournalReversal.Reverse(
            original, today, timeProvider.GetUtcNow(),
            new ReversalContext(reversedOn is not null, period.FiscalYearId));

        // **訂正は「取消 ＋ 再計上」なので、取り消せる伝票と訂正できる伝票は今のところ同じである。**
        // 「既に訂正されている」を別に見る必要は無い——訂正があるなら必ず取消もあるので、
        // 取消の判定（既に取り消されている）で先に落ちる。
        // 2 つの値に分けてあるのは、片方だけできる状態が将来生まれうるからである。
        return new AmendmentAvailability(reversal.Created, reversal.Created, Describe(reversal));
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
    public async Task<JournalEntryId> ReverseAsync(JournalEntryId originalId)
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
    public async Task<AmendmentStarted> CorrectAsync(JournalEntryId originalId)
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

    /// <summary>取消・訂正のどちらでも要る材料をまとめて用意する。</summary>
    private async Task<(JournalEntry Original, PostingContext Context, DateOnly Today, DateTimeOffset Now)>
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
        return (original, await masterLoader.LoadAsync(), DateOnly.FromDateTime(AccountingTimeZone.ToWallClock(now)), now);
    }

    /// <summary>
    /// 取消の可否を決める材料。<b>会計年度は取消の計上日（今日）から引く</b>ので、
    /// 該当する期間が無ければここで止める。
    /// </summary>
    private async Task<ReversalContext> ResolveReversalContextAsync(
        JournalEntry original, PostingContext context, DateOnly today)
    {
        if (context.Calendar.ResolvePeriod(today) is not AccountingPeriod period)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(
                    JournalViolationCodes.PeriodNotFound,
                    $"今日（{today:yyyy-MM-dd}）に対応する会計期間がありません。"),
            ]);
        }

        // 原仕訳の識別子は FindAsync が返した以上必ずある。
        var reversedOn = await entryStore.FindReversedOnAsync(original.Id!.Value);

        return new ReversalContext(reversedOn is not null, period.FiscalYearId);
    }

    /// <summary>下書きを書いて、<b>DB から読み直した姿</b>で計上する（qa/01 F-12 と同じ規律）。</summary>
    private async Task<JournalEntryId> PostDraftAsync(JournalEntry draft, PostingContext context)
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
/// <param name="Reason">できない理由。できるときは空文字。</param>
public readonly record struct AmendmentAvailability(bool CanReverse, bool CanCorrect, string Reason);

/// <summary>訂正を始めた結果。画面は再計上の下書きを開く。</summary>
/// <param name="ReversalId">計上した取消の識別子。</param>
/// <param name="CorrectionId">これから利用者が直す再計上の下書きの識別子。</param>
public readonly record struct AmendmentStarted(JournalEntryId ReversalId, JournalEntryId CorrectionId);
