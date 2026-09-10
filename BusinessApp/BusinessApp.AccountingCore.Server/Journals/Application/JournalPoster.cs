namespace BusinessApp.AccountingCore.Server.Journals.Application;

using System.Globalization;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.Partners.Server;

using Codeer.LowCode.Blazor.DataIO.Db;

using Codeer.LowCode.Blazor.DataIO;
using BusinessApp.AccountingCore.Server.Journals.Infrastructure;

/// <summary>
/// 下書きを計上して DB に印を付ける。<b>計上する経路はここ 1 本だけにする。</b>
/// </summary>
/// <remarks>
/// <para>「検証 → 写しを焼く → 採番 → 計上済みにする」の 4 つは必ず組で起きる。呼び出し側ごとに
/// 並べ直せる形にしておくと、<b>1 か所で採番を書き戻し忘れただけで伝票番号が重複する</b>し、
/// 検証を飛ばした計上が 1 経路でも生まれれば ADR-0004 の関門がまるごと迂回される。</para>
/// <para>使うのは 2 か所。画面の保存を包む関門（<see cref="JournalSubmitGate"/>）と、
/// 「訂正する」「取り消す」でサーバが自分から計上する経路（<see cref="JournalAmendmentService"/>）。</para>
/// </remarks>
public sealed class JournalPoster(
    JournalEntryStore entryStore,
    EntryNumberSequenceStore sequenceStore,
    LedgerSnapshotWriter snapshotWriter,
    AccountingMasterLoader masterLoader,
    TimeProvider timeProvider,
    IAuthenticationContext authenticationContext)
{
    /// <summary>
    /// 部品の組み立て。<b>本番もテストもここを通す。</b>
    /// </summary>
    /// <remarks>
    /// 計上する経路は 2 つある（保存の関門と、取消・訂正）。<b>組み立てを両方に書くと、
    /// 片方にだけ新しい部品を足したときに「その経路だけ写しが焼かれない」</b>という形で静かにずれる。
    /// </remarks>
    public static JournalPoster Create(
        IDbAccessor dbAccessor, string dataSourceName, JournalEntryStore entryStore,
        TimeProvider timeProvider, IAuthenticationContext authenticationContext)
        => new(entryStore,
               new EntryNumberSequenceStore(dbAccessor, dataSourceName),
               new LedgerSnapshotWriter(
                   dbAccessor, dataSourceName, new PartnerRegistrationStore(dbAccessor, dataSourceName)),
               new AccountingMasterLoader(dbAccessor, dataSourceName),
               timeProvider,
               authenticationContext);

    /// <summary>
    /// 下書きを計上する。違反があれば例外にして保存全体を巻き戻す。
    /// </summary>
    /// <param name="draft">計上する下書き。<b>DB から読み直した姿</b>を渡す（qa/01 F-12）。</param>
    /// <param name="masters">計上検証に要るマスタ一式（取引先はここで足す）。</param>
    /// <returns>計上済みになった伝票。</returns>
    public async Task<JournalEntry> PostAsync(JournalEntry draft, AccountingMasters masters)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Id is not JournalEntryId id)
        {
            throw new InvalidOperationException("保存されていない仕訳は計上できない。");
        }

        // **参照している取引先だけを目録に足す**（全件は読まない。<see cref="PartnerCatalog"/>）。
        // ここで足すので、画面の保存・取消・訂正の再計上のどの経路でも取引先の実在と有効が検査される。
        var context = await masterLoader.WithPartnersAsync(masters, draft);

        var sequence = await sequenceStore.ReadAsync(draft.FiscalYearId);
        var result = JournalPosting.Post(draft, context, sequence, timeProvider.GetUtcNow());

        if (!result.IsPosted)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        var postedBy = await ResolvePostedByAsync();

        // **計上済みにする前に焼く。** 計上済みの明細は DDL のトリガが UPDATE を止めるので、
        // 順番を入れ替えると写しが書けないのではなく、**計上そのものが落ちる**（ADR-0018）。
        var snapshotViolations = await snapshotWriter.BurnAsync(draft);
        if (snapshotViolations.Count > 0)
        {
            throw new JournalPostingRejectedException(snapshotViolations);
        }

        await sequenceStore.SaveAsync(sequence, result.NextSequence!.Value);
        await entryStore.MarkPostedAsync(id, result.EntryNo!.Value, result.PostedEntry!.PostedAt!.Value, postedBy);

        // 返すのは計上検証の結果。posted_by は DB にだけ書く（読み手は LoadAsync で読み直す）。
        // 戻り値に with で足しても誰も読まず、表明の無い契約になるだけである（2026-08-25 の自己レビュー）。
        return result.PostedEntry;
    }

    /// <summary>
    /// 計上した人（qa/02 R2-05）。<b>計上のたびに認証コンテキストから引く。</b>
    /// 取消・訂正もこの経路を通るので、原仕訳を計上した人ではなく<b>その操作をした人</b>が入る。
    /// </summary>
    /// <remarks>
    /// <para>識別子が<b>空</b>のとき（認証の無い経路）は null。列は NULL 可で、
    /// 「誰か分からない」を 0 などの偽の値で埋めない。</para>
    /// <para>識別子が<b>空でないのに正の整数として読めない</b>のは、認証の設定が壊れている
    /// （クレームの欠落・形式変更）。黙って null にすると、計上済みは不変（I-05）なので
    /// <b>記帳者の記録が永久に失われる</b>。音を立てて止める。</para>
    /// </remarks>
    private async Task<long?> ResolvePostedByAsync()
    {
        var userId = await authenticationContext.GetCurrentUserIdAsync();
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        if (!long.TryParse(userId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            throw new InvalidOperationException(
                $"認証されたユーザーの識別子が数値として読めない（値: {userId}）。認証の設定を確かめること。");
        }

        return value;
    }
}
