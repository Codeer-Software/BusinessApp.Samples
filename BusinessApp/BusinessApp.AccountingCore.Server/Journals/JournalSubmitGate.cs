namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Server.Shared;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 保存時の関門（ADR-0008）。仕訳の計上を捕まえて <see cref="JournalPosting"/> を通す。
/// </summary>
/// <remarks>
/// <para><b>入口は <see cref="SubmitAsync"/> の 1 本だけにしてある。</b> 保存の前後で
/// やることがあるが、それを 2 つの公開メソッドに分けると、順番を入れ替えても片方を
/// 呼び忘れてもコンパイルが通ってしまう。<b>呼び忘れた場合に起きるのは「計上したつもりの
/// 下書きが残る」ではなく「検証を一切通らずに計上済みが書かれる」</b>ので、
/// ADR-0004 の関門がまるごと迂回される。順番は型で保証する。</para>
/// <para>中でやっていること。</para>
/// <list type="number">
///   <item>保存の前 — 入力年月日を打ち、「計上済み」で送られてきた伝票を<b>下書きに戻す</b>。</item>
///   <item>保存（呼び出し側から渡された処理）。</item>
///   <item>保存の後 — 書かれた伝票を丸ごと読み直して検証し、通れば採番して計上済みにする。
///     違反があれば例外にして保存全体を巻き戻す。</item>
/// </list>
/// <para>いったん下書きとして書かせるのには理由が 2 つある。</para>
/// <list type="bullet">
///   <item><b>CLB は変更されたフィールドしか送ってこない</b>（qa/01 F-11・F-12）。
///     既存の下書きを開いて状態だけ変えた保存では、取引日も明細も差分に載らない。
///     差分だけを見て検証すると必ず誤判定する。</item>
///   <item>DDL のトリガが<b>計上済みの伝票への明細追加を止める</b>。
///     先に計上済みとして書くと、その伝票の明細が 1 行も入らない。</item>
/// </list>
/// <para>計上の経路は保存経路と同じ 1 本にしてある（利用者は状態を「計上済み」にして保存するだけ）。
/// 検証を通さずに <c>posted</c> にできる道を作らない（ADR-0004）。</para>
/// </remarks>
public sealed class JournalSubmitGate(
    AccountingMasterLoader masterLoader,
    JournalEntryStore entryStore,
    JournalReversalPosting reversalPosting,
    JournalCorrectionPosting correctionPosting,
    JournalPoster poster,
    TimeProvider timeProvider)
{
    public const string EntryModuleName = "JournalEntry";

    /// <summary>
    /// 部品の組み立て。<b>本番もテストもここを通す。</b>
    /// それぞれが手で組み立てると、本番の配線とテストの配線がずれても誰も気づけない。
    /// </summary>
    public static JournalSubmitGate Create(
        IDbAccessor dbAccessor, string dataSourceName, TimeProvider timeProvider,
        IAuthenticationContext authenticationContext)
    {
        var entryStore = new JournalEntryStore(dbAccessor, dataSourceName);
        return new JournalSubmitGate(
            new AccountingMasterLoader(dbAccessor, dataSourceName),
            entryStore,
            new JournalReversalPosting(entryStore),
            new JournalCorrectionPosting(entryStore),
            new JournalPoster(
                entryStore, new EntryNumberSequenceStore(dbAccessor, dataSourceName),
                timeProvider, authenticationContext),
            timeProvider);
    }

    // 状態の文字列は列挙子から導く。手で "posted" と書くと、列挙子や DB の値を変えたときに
    // 黙って一致しなくなり、計上ボタンが下書き保存に化ける（qa/01 の静かな失敗そのもの）。
    private static readonly string DraftStatus = DbValue.ToSnakeCase(EntryStatus.Draft);
    private static readonly string PostedStatus = DbValue.ToSnakeCase(EntryStatus.Posted);

    /// <summary>
    /// 保存を包む。<paramref name="save"/> は CLB 本来の保存処理。
    /// </summary>
    /// <remarks>
    /// <paramref name="save"/> を呼ぶ前と後にやることがあるので、呼び出し側に順番を委ねず
    /// ここで組み立てる。呼び出し側は「保存する処理」を渡すだけでよい。
    /// </remarks>
    public async Task<List<ModuleSubmitResult>> SubmitAsync(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<Task<List<ModuleSubmitResult>>> save)
    {
        ArgumentNullException.ThrowIfNull(transactionData);
        ArgumentNullException.ThrowIfNull(save);

        var pending = RewriteForDraftSave(transactionData);
        await RejectEntryTypeChangeAsync(transactionData);
        var results = await save();
        await PostAllAsync(pending, results);

        return results;
    }

    /// <summary>
    /// 既にある伝票の<b>種別を変える保存を、書く前に止める</b>。
    /// </summary>
    /// <remarks>
    /// <para>種別が変えられると、種別ごとの関門（<see cref="PostAsync"/> のホワイトリスト）が
    /// 丸ごと外れる。とくに「訂正」を「通常」に変えると、原仕訳との関係を見る検証を通らずに計上でき、
    /// しかも二重訂正の検出は <c>correction</c> の行しか数えないので、
    /// <b>同じ原仕訳にもう 1 本訂正を計上できてしまう</b>（＝取引が帳簿に 2 回載る）。</para>
    /// <para>DDL のトリガも同じことを止めるが、<b>トリガは最後の砦であって日常の分岐ではない</b>。
    /// 正常系でトリガに当てると、利用者には生の SQLite 例外しか届かない。</para>
    /// </remarks>
    private async Task RejectEntryTypeChangeAsync(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        foreach (var data in EntriesIn(transactionData, d => d.Update))
        {
            var submitted = GetSelect(data, "EntryType");
            if (submitted.Length == 0 || !long.TryParse(GetId(data), out var id))
            {
                continue;
            }

            var stored = await entryStore.FindEntryTypeAsync(new JournalEntryId(id));
            if (stored is { } current && DbValue.ToSnakeCase(current) != submitted)
            {
                throw new JournalPostingRejectedException(
                [
                    new Violation(
                        JournalViolationCodes.EntryTypeImmutable,
                        $"伝票の種別は変更できません（「{current.DisplayName()}」のままです）。"
                        + "種別を変えるときは、下書きを作り直してください。"),
                ]);
            }
        }
    }

    /// <summary>計上を待っている伝票。<see cref="SubmittedId"/> は保存前の値（仮 ID のことがある）。</summary>
    private readonly record struct PendingPosting(string SubmittedId);

    /// <summary>
    /// 送られてきた保存内容を<b>書き換える</b>。計上として送られてきた伝票を下書きに戻し、
    /// 入力年月日をシステムの値に差し替えて、計上待ちの控えを返す。
    /// </summary>
    private IReadOnlyList<PendingPosting> RewriteForDraftSave(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        // 伝票と明細は同じ ModuleSubmitData の Add / Update に混ざって届く（qa/01 F-11・F-12）。
        // ModuleSubmitData.ModuleName ではなく ModuleData.Name で見分ける。
        var added = EntriesIn(transactionData, d => d.Add);
        var updated = EntriesIn(transactionData, d => d.Update);

        // 入力年月日はシステムが決める。利用者からの値は採らない（docs/04 §2）。
        foreach (var data in added)
        {
            SetDateTime(data, "EnteredAt", AccountingTimeZone.ToWallClock(timeProvider.GetUtcNow()));
        }

        // 更新では**送られてきた入力年月日を捨てる**。DB のトリガも変更を拒むが、
        // 正常系でトリガに当てない。トリガは最後の砦であって、日常の分岐ではない。
        foreach (var data in updated)
        {
            data.Fields.Remove("EnteredAt");
        }

        var pending = new List<PendingPosting>();
        foreach (var data in added.Concat(updated).Where(d => GetSelect(d, "Status") == PostedStatus))
        {
            SetSelect(data, "Status", DraftStatus);
            pending.Add(new PendingPosting(GetId(data)));
        }

        return pending;
    }

    /// <summary>書かれた伝票を読み直して検証し、通れば計上済みにする。</summary>
    private async Task PostAllAsync(
        IReadOnlyList<PendingPosting> pending, IReadOnlyList<ModuleSubmitResult> results)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var idMap = TemporaryIdMap(results);
        var context = await masterLoader.LoadAsync();

        foreach (var item in pending)
        {
            await PostAsync(ResolveId(item.SubmittedId, idMap), context);
        }
    }

    private async Task PostAsync(JournalEntryId id, PostingContext context)
    {
        var draft = await entryStore.LoadAsync(id);

        // **種別ごとに通ってよい経路を決める（ホワイトリスト）。** 未実装の種別
        // （期首残高・決算振替・繰越）を素通りさせると、それぞれの前提を満たさないまま
        // 帳簿に載る。訂正を素通りさせた場合はもっと直接的で、原仕訳が生きたまま
        // 再計上が載り、取引が二重に計上される。
        draft = draft.EntryType switch
        {
            EntryType.Normal => draft,
            EntryType.Reversal => await reversalPosting.ApplyAsync(draft, context),
            EntryType.Correction => await correctionPosting.ApplyAsync(draft),
            _ => throw new JournalPostingRejectedException(
            [
                new Violation(
                    JournalViolationCodes.EntryTypeNotSupported,
                    $"種別が「{draft.EntryType.DisplayName()}」の伝票は、まだ計上できません。"),
            ]),
        };

        await poster.PostAsync(draft, context);
    }

    /// <summary>
    /// 仮 ID から本物の ID への対応表。
    /// <b>同じ仮 ID が二重に来たら止める。</b> 先勝ちで捨てると、片方が黙って別の伝票に化ける。
    /// </summary>
    private static Dictionary<string, string> TemporaryIdMap(IReadOnlyList<ModuleSubmitResult> results)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (temporary, real) in results.SelectMany(r => r.TemporaryIdMap))
        {
            if (map.TryGetValue(temporary, out var existing) && existing != real)
            {
                throw new InvalidOperationException(
                    $"仮 ID {temporary} に本物の ID が 2 つ対応している（{existing} と {real}）。");
            }

            map[temporary] = real;
        }

        return map;
    }

    /// <summary>新規保存では ID が仮のままなので、保存結果の対応表で本物に置き換える。</summary>
    private static JournalEntryId ResolveId(string submittedId, IReadOnlyDictionary<string, string> idMap)
    {
        var resolved = idMap.TryGetValue(submittedId, out var real) ? real : submittedId;

        return long.TryParse(resolved, out var value)
            ? new JournalEntryId(value)
            : throw new InvalidOperationException(
                $"計上する仕訳の ID を解決できない（送られてきた値: {submittedId}）。");
    }

    /// <summary>保存内容から伝票だけを拾う。明細は同じ束に混ざっている（qa/01 F-11）。</summary>
    private static List<ModuleData> EntriesIn(
        IReadOnlyList<ModuleSubmitData> transactionData,
        Func<ModuleSubmitData, List<ModuleData>> part)
        => transactionData.SelectMany(part).Where(d => d.Name == EntryModuleName).ToList();

    private static string GetId(ModuleData data)
        => Field<IdFieldData>(data, "Id")?.Value ?? string.Empty;

    private static string GetSelect(ModuleData data, string name)
        => Field<SelectFieldData>(data, name)?.Value ?? string.Empty;

    private static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
        => data.Fields.TryGetValue(name, out var field) ? field as T : null;

    private static void SetSelect(ModuleData data, string name, string value)
        => Ensure<SelectFieldData>(data, name).Value = value;

    private static void SetDateTime(ModuleData data, string name, DateTime value)
        => Ensure<DateTimeFieldData>(data, name).Value = value;

    /// <summary>
    /// フィールドを書ける状態にして返す。<b>無ければ作る。</b>
    /// CLB は変更されたフィールドしか送ってこないので、画面で触っていない項目
    /// （入力年月日など）は差分に載っていない（qa/01 F-11・F-12）。
    /// </summary>
    private static T Ensure<T>(ModuleData data, string name) where T : FieldDataBase, new()
    {
        if (Field<T>(data, name) is { } existing)
        {
            return existing;
        }

        var created = new T();
        data.Fields[name] = created;
        return created;
    }
}
