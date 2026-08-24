namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 保存時の関門（ADR-0008）。仕訳の計上を捕まえて <see cref="JournalPosting"/> を通す。
/// </summary>
/// <remarks>
/// <para><b>保存を挟んで二段で動く。</b></para>
/// <list type="number">
///   <item><see cref="Prepare"/> — 保存の前。入力年月日を打ち、
///     「計上済み」で送られてきた伝票をいったん<b>下書きに戻す</b>。</item>
///   <item><see cref="CompleteAsync"/> — 保存の後。書かれた伝票を丸ごと読み直して検証し、
///     通れば採番して計上済みにする。違反があれば例外にして保存全体を巻き戻す。</item>
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
    EntryNumberSequenceStore sequenceStore,
    TimeProvider timeProvider)
{
    public const string EntryModuleName = "JournalEntry";

    private const string DraftStatus = "draft";
    private const string PostedStatus = "posted";

    /// <summary>計上を待っている伝票。<see cref="SubmittedId"/> は保存前の値（仮 ID のことがある）。</summary>
    public readonly record struct PendingPosting(string SubmittedId);

    /// <summary>
    /// 保存の前に呼ぶ。計上として送られてきた伝票を下書きに戻し、後段に渡す控えを返す。
    /// </summary>
    public IReadOnlyList<PendingPosting> Prepare(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        ArgumentNullException.ThrowIfNull(transactionData);

        // 伝票と明細は同じ ModuleSubmitData の Add / Update に混ざって届く（qa/01 F-11・F-12）。
        // ModuleSubmitData.ModuleName ではなく ModuleData.Name で見分ける。
        var added = EntriesIn(transactionData, d => d.Add);
        var changed = new List<ModuleData>(added);
        changed.AddRange(EntriesIn(transactionData, d => d.Update));

        // 入力年月日はシステムが決める。利用者からの値は採らない（docs/04 §2）。
        // 新規のときだけ打つ。既存の値は DB のトリガが変更を拒む。
        foreach (var data in added)
        {
            SetDateTime(data, "EnteredAt", timeProvider.GetUtcNow().LocalDateTime);
        }

        var pending = new List<PendingPosting>();
        foreach (var data in changed.Where(d => GetSelect(d, "Status") == PostedStatus))
        {
            SetSelect(data, "Status", DraftStatus);
            pending.Add(new PendingPosting(GetId(data)));
        }

        return pending;
    }

    /// <summary>
    /// 保存の後に呼ぶ。書かれた伝票を読み直して検証し、通れば計上済みにする。
    /// </summary>
    public async Task CompleteAsync(
        IReadOnlyList<PendingPosting> pending,
        IReadOnlyList<ModuleSubmitResult> results)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(results);

        if (pending.Count == 0)
        {
            return;
        }

        var idMap = results
            .Where(r => r.TemporaryIdMap is not null)
            .SelectMany(r => r.TemporaryIdMap)
            .DistinctBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var context = await masterLoader.LoadAsync();

        foreach (var item in pending)
        {
            await PostAsync(ResolveId(item.SubmittedId, idMap), context);
        }
    }

    private async Task PostAsync(JournalEntryId id, PostingContext context)
    {
        var draft = await entryStore.LoadAsync(id);
        var sequence = await sequenceStore.ReadAsync(draft.FiscalYearId);
        var result = JournalPosting.Post(draft, context, sequence, timeProvider.GetUtcNow());

        if (!result.IsPosted)
        {
            throw new JournalPostingRejectedException(result.Violations);
        }

        await sequenceStore.SaveAsync(sequence, result.NextSequence!.Value);
        await entryStore.MarkPostedAsync(
            id, result.PostedEntry!.EntryNo!.Value, result.PostedEntry.PostedAt!.Value);
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
