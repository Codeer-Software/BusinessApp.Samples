namespace BusinessApp.AccountingCore.Server.Journals;

using System.Globalization;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Shared;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.Partners.Server;
using BusinessApp.ServerSupport;
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
            JournalPoster.Create(dbAccessor, dataSourceName, entryStore, timeProvider, authenticationContext),
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

        // **削除は別に断る。** 見出しは押したボタンで決まる（qa/02 R24-23）ので、
        // 「削除」を押した人に「保存できません」と言わない。
        await RejectDeletionsAsync(transactionData);

        // **見出しは操作で決める。** 計上待ちが 1 件でもあれば、利用者は「計上する」を押している。
        // 関門ごとに決めると、同じ違反でも捕まえた場所で言葉が変わる。
        await RejectBeforeSaveAsync(transactionData, pending.Count > 0);
        var results = await save();

        // **保存が失敗していたら、計上へ進まない。**
        // CLB は保存の失敗を例外ではなく ExceptionMessage に詰めて返す。見ずに先へ進むと、
        // 「仮 ID を解決できない」という二次的な内部エラーに化けて、**本当の理由が利用者に届かない**
        // （2026-08-26 の実機操作テストで発見。qa/03 L-10）。
        // ここで返せば、保存の失敗は CLB 本来の経路がそのまま報告する。
        if (results.Exists(result => !string.IsNullOrEmpty(result.ExceptionMessage)))
        {
            return results;
        }

        await PostAllAsync(pending, results);

        return results;
    }

    /// <summary>
    /// <b>保存へ渡す前の検査。違反は全部集めてから 1 回で投げる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>見つけた順に投げない。</b> 種別で 1 回・明細で 1 回と分けて投げると、
    /// 利用者は種別を直して保存し直してから明細の差し戻しを受ける——
    /// <see cref="JournalPostingRejectedException"/> が全件を並べる理由（直しては弾かれを繰り返させない）を、
    /// 関門どうしの間で破ることになる。</para>
    /// <para><b>見出しは押されたボタンで決める。</b> ここで止まるものは計上かどうかに関係なく
    /// 保存が失敗する（DDL の <c>NOT NULL</c> も、種別を変えないトリガも、下書き保存で当たる）が、
    /// <b>利用者が知りたいのは「自分が押した操作がどうなったか」</b>である——
    /// 「計上する」を押して「保存できません」と言われると、別の操作を断られたように読める。</para>
    /// <para><b>保存の前に投げる。</b> 保存に渡してしまうと、失敗は例外ではなく
    /// <c>ExceptionMessage</c> で返り、CLB が枠組みの言葉をそのままトーストに出す（qa/01 F-16）。</para>
    /// </remarks>
    /// <param name="isPosting">計上として送られてきた伝票が 1 件でもあるか。</param>
    private async Task RejectBeforeSaveAsync(
        IReadOnlyList<ModuleSubmitData> transactionData, bool isPosting)
    {
        var violations = new List<Violation>(JournalSubmitRequirements.Check(transactionData));
        violations.AddRange(await EntryTypeChangesAsync(transactionData));

        if (violations.HasError())
        {
            throw new JournalPostingRejectedException(
                violations,
                isPosting
                    ? JournalPostingRejectedException.PostingHeadline
                    : JournalPostingRejectedException.SavingHeadline);
        }
    }

    /// <summary>
    /// <b>消せない伝票の削除を、保存へ渡す前に止める。</b>
    /// </summary>
    /// <remarks>
    /// <para>削除は「下書きを捨てる」操作である。一覧をクエリモジュールにして標準の削除アイコンが
    /// 無くなったので、詳細画面のボタンへ移した（ADR-0027 §4。2026-08-28 開発者が承認）。</para>
    /// <para>止めるのは 2 つ。</para>
    /// <list type="number">
    ///   <item><b>計上済み</b>——計上済みは不変である（ADR-0004）。取消か訂正で表す。</item>
    ///   <item><b>締め済みの会計期間・会計年度に属するもの</b>——ADR-0027 §4 が求めた形。</item>
    /// </list>
    /// <para><b>DDL のトリガも計上済みの削除を拒むが、トリガは最後の砦であって日常の分岐ではない。</b>
    /// 正常系でトリガに当てると、利用者には生の SQLite の例外がそのまま出る（qa/01 F-16）。</para>
    /// <para><b>会計期間が無い伝票は止めない。</b> 期間が無ければ締めようもなく、
    /// 期間の設定を直すまで消せない下書きが残るほうが困る。</para>
    /// <para><b>締めの側は、いまのところ完結していない。</b> 締め済み期間の下書きは
    /// <b>編集を止めていない</b>ので、計上日を開いている期間へ動かしてから消せば通る。
    /// 下書きは帳簿に載っていない（帳簿は <c>status = 'posted'</c> だけを出す）ので、
    /// 消しても帳簿は 1 行も動かない——<b>この関門が守っているのは「締めた期間は触らない」という
    /// 建前だけ</b>である。締め（月次締め・年度締め）を実装するフェーズ 4 で、
    /// <b>編集も止めるか、下書きは締めの対象外にするか</b>を揃えて決める
    /// （2026-08-31 の自己レビューで、理由文が実態と合っていないと指摘された。qa/02）。</para>
    /// </remarks>
    private async Task RejectDeletionsAsync(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        var deleted = DeletedEntryIdsIn(transactionData);
        if (deleted.Count == 0)
        {
            // **削除が無い保存でマスタを読みに行かない。** 保存のたびに会計年度と期間を
            // 引くのは、いちばん多い経路（下書き保存）に無駄な往復を足すことになる。
            return;
        }

        var calendar = (await masterLoader.LoadAsync()).Calendar;
        var violations = new List<Violation>();

        foreach (var submittedId in deleted)
        {
            // 保存されていない行（仮 ID）は、消しても帳簿は動かない。
            if (!long.TryParse(submittedId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                || await entryStore.FindAsync(new JournalEntryId(id)) is not JournalEntry stored)
            {
                continue;
            }

            if (stored.Status == EntryStatus.Posted)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AlreadyPosted,
                    $"計上済みの伝票（伝票番号 {stored.EntryNo}）は削除できません。"
                    + "取り消すか、訂正してください。"));
            }
            else if (calendar.ResolvePeriod(stored.PostingDate) is not null
                     && !calendar.IsPostable(stored.PostingDate))
            {
                violations.Add(new Violation(
                    JournalViolationCodes.PeriodClosed,
                    $"計上日（{stored.PostingDate:yyyy/MM/dd}）の会計期間は締められているので、"
                    + "この伝票は削除できません。締めを解除してから削除してください。"));
            }
        }

        if (violations.HasError())
        {
            throw new JournalPostingRejectedException(
                violations, JournalPostingRejectedException.DeletionHeadline);
        }
    }

    /// <summary>
    /// 既にある伝票の<b>種別を変える保存</b>を見つける。
    /// </summary>
    /// <remarks>
    /// <para>種別が変えられると、種別ごとの関門（<see cref="PostAsync"/> のホワイトリスト）が
    /// 丸ごと外れる。とくに「訂正」を「通常」に変えると、原仕訳との関係を見る検証を通らずに計上でき、
    /// しかも二重訂正の検出は <c>correction</c> の行しか数えないので、
    /// <b>同じ原仕訳にもう 1 本訂正を計上できてしまう</b>（＝取引が帳簿に 2 回載る）。</para>
    /// <para>DDL のトリガも同じことを止めるが、<b>トリガは最後の砦であって日常の分岐ではない</b>。
    /// 正常系でトリガに当てると、利用者には生の SQLite 例外しか届かない。</para>
    /// </remarks>
    private async Task<List<Violation>> EntryTypeChangesAsync(IReadOnlyList<ModuleSubmitData> transactionData)
    {
        var violations = new List<Violation>();

        foreach (var data in EntriesIn(transactionData, d => d.Update))
        {
            var submitted = GetSelect(data, "EntryType");
            if (submitted.Length == 0
                || !long.TryParse(GetId(data), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var stored = await entryStore.FindEntryTypeAsync(new JournalEntryId(id));
            if (stored is EntryType current && DbValue.ToSnakeCase(current) != submitted)
            {
                // **「下書きを作り直す」とは書かない。** 訂正の下書きでそう言われても、
                // 元の伝票は既に取り消してあるので同じものをもう一度は作れない（行き止まりになる）。
                violations.Add(new Violation(
                    JournalViolationCodes.EntryTypeImmutable,
                    $"伝票の種別は、保存したあとは変更できません（「{current.DisplayName()}」のままです）。"
                    + "別の種別で起票するときは、新しい振替伝票を作成してください。"));
            }
        }

        return violations;
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
            SetDateTime(data, "EnteredAt", DatabaseTimeZone.ToWallClock(timeProvider.GetUtcNow()));
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
    /// </summary>
    /// <remarks>
    /// <para><b>対応は <c>SourceId</c>（送った仮 ID）→ <c>DestinationId</c>（採番された本物の ID）で返る。</b>
    /// 同じ型が持つ <c>TemporaryIdMap</c> のほうには**入らない**（実測。2026-08-26）。</para>
    /// <para><b>ここを取り違えると、新規作成の画面から計上したときだけ落ちる</b>——
    /// 下書き保存を挟めば本物の ID で送られてくるので通ってしまい、
    /// 「保存してから計上する」経路しか試していないと気づけない
    /// （実機操作テストで発見。qa/03 L-10）。</para>
    /// <para><b>同じ仮 ID が二重に来たら止める。</b> 先勝ちで捨てると、片方が黙って別の伝票に化ける。</para>
    /// </remarks>
    private static Dictionary<string, string> TemporaryIdMap(IReadOnlyList<ModuleSubmitResult> results)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var result in results.Where(r => !string.IsNullOrEmpty(r.SourceId)))
        {
            if (map.TryGetValue(result.SourceId, out var existing) && existing != result.DestinationId)
            {
                throw new InvalidOperationException(
                    $"仮 ID {result.SourceId} に本物の ID が 2 つ対応している（{existing} と {result.DestinationId}）。");
            }

            map[result.SourceId] = result.DestinationId;
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

    /// <summary>
    /// 削除しようとしている伝票の識別子。
    /// </summary>
    /// <remarks>
    /// <para><b>削除だけ器が違う。</b> 追加・更新は <c>ModuleData</c>（フィールドの束）で来るが、
    /// 削除は <c>ModuleDeleteInfo</c>（識別子とモジュール名だけ）で来る。
    /// <see cref="EntriesIn"/> をそのまま使えないのはそのためである。</para>
    /// <para><b><c>ModuleSubmitData.SearchDelete</c>（検索条件で消す経路）は見ていない。</b>
    /// 見なくてよい根拠は<b>設計側にある</b>——本プロジェクトのどの <c>ListField</c> も
    /// <c>ReplaceMode</c> が <c>"None"</c> なので、この経路は発火しない。
    /// <b>入れた日に、明細の一括削除が関門を通らず DDL のトリガの生の例外として出る</b>
    /// （qa/01 F-16 の形）。<c>ReplaceMode</c> を触るときは、ここも一緒に見ること。</para>
    /// </remarks>
    private static List<string> DeletedEntryIdsIn(IReadOnlyList<ModuleSubmitData> transactionData)
        => [.. transactionData
            .SelectMany(d => d.Delete)
            .Where(d => d.ModuleName == EntryModuleName)
            .Select(d => d.Id)];

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
        if (Field<T>(data, name) is T existing)
        {
            return existing;
        }

        var created = new T();
        data.Fields[name] = created;
        return created;
    }
}
