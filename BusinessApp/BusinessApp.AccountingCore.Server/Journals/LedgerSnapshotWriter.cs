namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.Partners;
using BusinessApp.Partners.Server;
using BusinessApp.AccountingCore.Shared;

using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 計上のときに、帳簿の記載事項をマスタから写して明細に固定する
/// （<see href="../../../docs/decisions/0018-帳簿の記載事項は計上時に写して固定する.md">ADR-0018</see>）。
/// </summary>
/// <remarks>
/// <para>写すのは 2 つ。</para>
/// <list type="table">
///   <item>
///     <term><c>partner_name_snapshot</c></term>
///     <description><b>帳簿の法定記載事項</b>（消法 30 ⑧の「課税仕入れの相手方の氏名又は名称」）。
///       取引先が改名しても過去の帳簿の記載が変わらないようにする。</description>
///   </item>
///   <item>
///     <term><c>registration_no_snapshot</c></term>
///     <description><b>法定記載事項ではない</b>（帳簿に相手方の登録番号は要らない）。
///       持つのは<b>判定の根拠の記録</b>のためで、帳簿には印字しない。
///       <b>この行の「取引先」の番号であって、自社の番号ではない。</b></description>
///   </item>
/// </list>
/// <para><b>利用者に入力させない。</b> 画面に写しを出すと任意の名前を書けてしまい、
/// 法定記載事項の信頼性が落ちる（ADR-0018）。サーバがマスタから引いて書く。
/// <b>取引先の無い行には NULL を焼く</b>——飛ばすと、利用者が送ってきた文字列が残る。</para>
/// <para><b>明細に取引先が無い行には伝票の取引先を使う。</b> 帳簿は「明細 → 伝票」の順に
/// 取引先を見る（docs/10 §4-1）ので、写しも同じ順で決める。</para>
/// <para><b>取消（反対仕訳）には焼き直さない。</b> 反対仕訳は過去の反転であって新しい記帳ではないので、
/// 原仕訳から写した値をそのまま残す（ADR-0018）。焼き直すと、原仕訳の計上後に改名された相手で
/// <b>同じ取引の表と裏が違う名前になる</b>（実機操作テストで発見。qa/03 L-13）。</para>
/// <para><b>呼ぶのは計上済みにする前。</b> 計上済みの明細は DDL のトリガが
/// UPDATE を止めるので、順番を入れ替えると計上そのものが落ちる
/// （<see cref="JournalPoster"/> が順番を持っている）。</para>
/// </remarks>
public sealed class LedgerSnapshotWriter(IDbAccessor dbAccessor, string dataSourceName, PartnerRegistrationStore partners)
{
    /// <summary>下書きの明細に写しを焼く。</summary>
    /// <param name="draft">計上しようとしている下書き（<b>DB から読み直した姿</b>）。</param>
    public async Task BurnAsync(JournalEntry draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Id is not JournalEntryId id)
        {
            throw new InvalidOperationException("保存されていない仕訳には写しを焼けない。");
        }

        // 取消は原仕訳の写しを引き継ぐ（ADR-0018）。訂正の再計上は利用者が取引先を直せるので焼き直す。
        if (draft.EntryType == EntryType.Reversal)
        {
            await InheritEntryNameAsync(id);
            return;
        }

        // 同じ取引先が何行にも出るのが普通なので、取引先ごとに 1 回だけ読む。
        var cache = new Dictionary<long, PartnerSnapshot>();

        await WriteEntryNameAsync(
            id,
            draft.PartnerId is PartnerId entryPartnerId
                ? (await CachedAsync(cache, entryPartnerId)).Name
                : null);

        foreach (var line in draft.Lines)
        {
            var snapshot = PartnerOf(draft, line) is PartnerId partnerId
                ? await CachedAsync(cache, partnerId)
                : PartnerSnapshot.None;

            await WriteAsync(id, line.LineNo, snapshot.Name, RegistrationNoAt(snapshot, TaxPointOf(draft, line)));
        }
    }

    /// <summary>
    /// この行の取引先。<b>明細が持っていなければ伝票のものを使う</b>（docs/10 §4-1）。
    /// </summary>
    private static PartnerId? PartnerOf(JournalEntry entry, JournalLine line) => line.PartnerId ?? entry.PartnerId;

    /// <summary>
    /// この行の課税仕入れの時点。<b>入っていなければ伝票の取引日を使う</b>。
    /// </summary>
    /// <remarks>
    /// <para><c>tax_point</c> は「課税仕入れを行った日」であり、
    /// 引き渡しが取引日と違う取引のために<b>行ごとに上書きできる</b>ようにしてある。
    /// 上書きされていなければ<b>取引日がその日</b>である——別の日を書いていないのだから、
    /// 取引の日に行われたと読むのが素直である。</para>
    /// <para><b>「入っていなければ写さない」にはしない。</b> いまの振替伝票の画面は
    /// <c>tax_point</c> を入力させないので、写しが<b>永久に空</b>になる
    /// （実機操作テストで発見。qa/03 L-12）。計上済みは不変（ADR-0004）なので、
    /// あとから埋め直せない。</para>
    /// <para><b>恒久の解は画面に <c>tax_point</c> を持たせること。</b>
    /// 支払日で起票する未払金の決済や締め日基準の一括計上では、取引日と課税仕入れの日がずれる
    /// （docs/11 §5）。[docs/13 の保留リスト](../../../docs/13_取引先設計.md)に積んである。</para>
    /// </remarks>
    private static DateOnly TaxPointOf(JournalEntry entry, JournalLine line)
        => line.TaxPoint ?? entry.TransactionDate;

    /// <summary>その日の登録番号。無ければ <c>null</c>。</summary>
    /// <remarks>
    /// <b>どれを写すか決められないときは、利用者のことばにして止める。</b>
    /// 純粋関数（<see cref="InvoiceRegistrationHistory"/>）が投げるのは開発者向けの文言なので、
    /// ここで計上の差し戻しに載せ替える（docs/21_画面の原則.md §2）。
    /// </remarks>
    private static string? RegistrationNoAt(PartnerSnapshot snapshot, DateOnly taxPoint)
    {
        try
        {
            return InvoiceRegistrationHistory.InEffectOn(snapshot.Registrations, taxPoint)?.RegistrationNo;
        }
        catch (InvalidOperationException)
        {
            throw new JournalPostingRejectedException(
            [
                new Violation(
                    JournalViolationCodes.AmbiguousRegistration,
                    $"取引先「{snapshot.Name}」には {taxPoint:yyyy/MM/dd} 時点で始まる登録が 2 件あります。"
                    + "サイドバーの「登録番号」で、どちらかの登録年月日を直してください。"),
            ]);
        }
    }

    private async Task<PartnerSnapshot> CachedAsync(Dictionary<long, PartnerSnapshot> cache, PartnerId partnerId)
    {
        if (!cache.TryGetValue(partnerId.Value, out var snapshot))
        {
            snapshot = new PartnerSnapshot(
                await partners.FindNameAsync(partnerId),
                await partners.LoadRegistrationsAsync(partnerId));
            cache[partnerId.Value] = snapshot;
        }

        return snapshot;
    }

    /// <summary>
    /// 1 行に写しを書く。<b>1 行に当たらなければ止める。</b>
    /// </summary>
    /// <remarks>
    /// 黙って 0 件で通すと、計上済みは不変（ADR-0004）なので<b>写しが永久に空のまま残る</b>。
    /// この設計がいちばん恐れている失敗が、音を立てずに起きる。
    /// </remarks>
    private async Task WriteAsync(JournalEntryId id, int lineNo, string? name, string? registrationNo)
    {
        var affected = await dbAccessor.ExecuteAsync(
            dataSourceName,
            """
            update journal_lines
               set partner_name_snapshot = @p3, registration_no_snapshot = @p4
             where journal_entry_id = @p1 and line_no = @p2
            """,
            new()
            {
                { "@p1", id.Value },
                { "@p2", lineNo },
                { "@p3", name },
                { "@p4", registrationNo },
            });

        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"仕訳 {id.Value} の {lineNo} 行目に写しを書けなかった（{affected} 行）。");
        }
    }

    /// <summary>
    /// 伝票の取引先の名前を焼く。<b>画面が計上時の姿を見せるためのもの</b>（ADR-0037）。
    /// </summary>
    /// <remarks>
    /// <b>明細の写しとは役割が違う。</b> あちらは帳簿の法定記載事項（消法 30 ⑧）で、
    /// こちらは伝票の詳細と入力の一覧が「計上したときに選んでいた取引先」を出すためのものである。
    /// <b>取引先の無い伝票には NULL を焼く</b>——飛ばすと、利用者が送ってきた文字列が残る。
    /// </remarks>
    private Task WriteEntryNameAsync(JournalEntryId id, string? name)
        => ExecuteOnEntryAsync(
            id,
            "update journal_entries set partner_name_snapshot = @p2 where id = @p1",
            new() { { "@p2", name } },
            "焼けなかった");

    /// <summary>
    /// 反対仕訳に、原仕訳の写しを引き継ぐ。<b>焼き直さない</b>（ADR-0018）。
    /// </summary>
    /// <remarks>
    /// 焼き直すと、原仕訳の計上後に改名された相手で<b>同じ取引の表と裏が違う名前になる</b>
    /// （明細で実際に起きた。qa/03 L-13）。取消の <c>partner_id</c> は原仕訳の写しなので、
    /// 名前も原仕訳のものをそのまま持ってくる。
    /// </remarks>
    private Task InheritEntryNameAsync(JournalEntryId id)
        => ExecuteOnEntryAsync(
            id,
            """
            update journal_entries
               set partner_name_snapshot =
                   (select o.partner_name_snapshot
                      from journal_entries o where o.id = journal_entries.original_entry_id)
             where id = @p1
            """,
            [],
            "原仕訳から引き継げなかった");

    /// <summary>
    /// 伝票 1 件に書く。<b>1 件に当たらなければ止める。</b>
    /// </summary>
    /// <remarks>
    /// 黙って 0 件で通すと、計上済みは不変（ADR-0004）なので<b>写しが永久に空のまま残る</b>。
    /// 明細側（<see cref="WriteAsync"/>）と同じ理由・同じ守り方である。
    /// </remarks>
    private async Task ExecuteOnEntryAsync(
        JournalEntryId id, string sql, Dictionary<string, object?> parameters, string what)
    {
        // **渡された辞書を書き換えない。** いまはどちらの呼び出しも新しいリテラルだが、
        // 使い回した辞書を渡した日に静かに壊れる形にしない。
        var bound = new Dictionary<string, object?>(parameters) { ["@p1"] = id.Value };

        var affected = await dbAccessor.ExecuteAsync(dataSourceName, sql, bound);
        if (affected != 1)
        {
            // **経路で文言を分ける。** 引き継ぎが 0 件なのは「原仕訳ごと消えている」という
            // 別の壊れ方なので、同じ文言だと調査が遠回りになる。
            throw new InvalidOperationException(
                $"伝票 {id.Value} の取引先名の写しを{what}（{affected} 行）。");
        }
    }

    /// <summary>1 つの取引先について、計上のときに要るものだけ。</summary>
    private readonly record struct PartnerSnapshot(string? Name, IReadOnlyList<InvoiceRegistration> Registrations)
    {
        /// <summary>取引先が無い行のための空。<b>飛ばさずにこれを焼く</b>ので NULL が入る。</summary>
        public static PartnerSnapshot None => new(null, []);
    }
}
