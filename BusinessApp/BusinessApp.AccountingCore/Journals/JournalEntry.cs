namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.Partners;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 仕訳伝票（docs/10 §4-1）。日付は用途ごとに別の項目で持ち、1 つに潰さない（docs/10 §2）。
/// </summary>
public sealed record JournalEntry
{
    /// <summary>識別子。まだ保存していない伝票は null。</summary>
    public JournalEntryId? Id { get; init; }

    /// <summary>属する会計年度。伝票番号の採番単位でもある。</summary>
    public required FiscalYearId FiscalYearId { get; init; }

    /// <summary>
    /// 伝票番号。計上時に採番し、欠番を埋め直さず再利用もしない（I-17）。下書きでは null。
    /// </summary>
    public int? EntryNo { get; init; }

    /// <summary>取引日。帳簿の「取引年月日」であり、法定記載事項②。</summary>
    public required DateOnly TransactionDate { get; init; }

    /// <summary>計上日。会計期間への帰属を決める（I-03）。</summary>
    public required DateOnly PostingDate { get; init; }

    public required EntryStatus Status { get; init; }

    public required EntryType EntryType { get; init; }

    /// <summary>原仕訳。訂正・取消では必須（I-06）。</summary>
    public JournalEntryId? OriginalEntryId { get; init; }

    public string? Description { get; init; }

    public PartnerId? PartnerId { get; init; }

    /// <summary>投入元の部品名。手入力は null（docs/10 §10）。</summary>
    public string? SourceComponent { get; init; }

    public string? SourceDocumentId { get; init; }

    /// <summary>外部投入の重複防止キー（I-14）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// 入力年月日。<b>利用者は変更できない</b>。
    /// 「通常の業務処理期間の経過後の入力の事実を確認できる」という優良な電子帳簿の要件
    /// （電帳規則 5 ⑤一イ(2)）そのものであり、取引日と共用しない（docs/10 §2）。
    /// </summary>
    public required DateTimeOffset EnteredAt { get; init; }

    /// <summary>計上日時。システムが付ける。下書きでは null。</summary>
    public DateTimeOffset? PostedAt { get; init; }

    /// <summary>
    /// 計上した人（認証部品のユーザー識別子）。システムが付ける。下書きでは null。
    /// 計上済みでも null がありうる（この列より前に計上された伝票）。
    /// </summary>
    /// <remarks>
    /// 型付き識別子（ADR-0014）にしない。ユーザーは会計コアの実体ではなく（ADR-0029）、
    /// 会計コアはこの値を解釈せず、外部キーも張らずに（ddl/README）書き写すだけである。
    /// <c>creator</c> / <c>updater</c>（CLB の予約列）と同じ扱い。
    /// </remarks>
    public long? PostedBy { get; init; }

    public required IReadOnlyList<JournalLine> Lines { get; init; }

    /// <summary>借方合計。</summary>
    public Yen DebitTotal => Total(DebitCredit.Debit);

    /// <summary>貸方合計。</summary>
    public Yen CreditTotal => Total(DebitCredit.Credit);

    /// <summary>借方合計と貸方合計が一致しているか（I-01）。</summary>
    public bool IsBalanced => DebitTotal == CreditTotal;

    /// <summary>
    /// 渡した明細の<b>基準日</b>——課税仕入れの日（<c>tax_point</c>）、入っていなければ伝票の取引日（docs/11 §5-2）。
    /// </summary>
    /// <remarks>
    /// <para><c>tax_point</c> は「課税仕入れを行った日」であり、
    /// 引き渡しが取引日と違う取引のために<b>行ごとに上書きできる</b>ようにしてある。
    /// 上書きされていなければ<b>取引日がその日</b>である——別の日を書いていないのだから、
    /// 取引の日に行われたと読むのが素直である。</para>
    /// <para><b>この定義は 1 か所にしか置かない。</b> 計上時の登録番号の写し
    /// （<c>LedgerSnapshotWriter</c>）と、取消・訂正の確認文（<c>JournalAmendmentService</c>）が
    /// 同じ日を見る——<b>写した日と、確認文が名指しする日が違うと、帳簿と画面が別の課税期間の話をする</b>
    /// （2026-09-22 の自己レビュー。docs/20 §4 の重複定義）。</para>
    /// </remarks>
    public DateOnly BasisDateOf(JournalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.TaxPoint ?? TransactionDate;
    }

    /// <summary>
    /// この伝票の明細が持つ基準日のうち、<b>いちばん古いもの</b>（docs/11 §5-2）。明細が無ければ取引日。
    /// </summary>
    /// <remarks>
    /// <b>明細が 1 行でも過年度の課税期間に属していれば、伝票全体をそう扱う</b>ためである。
    /// <b>いちばん新しい日を採ると</b>、過年度の税額が変わるのに何も出ない。
    /// </remarks>
    public DateOnly EarliestBasisDate
        => Lines.Count == 0 ? TransactionDate : Lines.Min(BasisDateOf);

    /// <summary>
    /// この明細の取引先。<b>明細が持っていなければ伝票のものを使う</b>（docs/10 §4-1）。
    /// </summary>
    /// <remarks>
    /// <b>帳簿に載る取引先はこの値である。</b> 写しを書く <c>LedgerSnapshotWriter</c> と
    /// 計上の関門（<c>E-PARTNER-REQUIRED</c>）が同じ値を見るように、ここ 1 か所に置いてある——
    /// 別々に書くと、<b>関門が通した行が帳簿では取引先なしになる</b>ずれを作れてしまう。
    /// </remarks>
    public PartnerId? PartnerOf(JournalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.PartnerId ?? PartnerId;
    }

    private Yen Total(DebitCredit side)
        => Lines.Where(l => l.DebitCredit == side).Select(l => l.Amount).Sum();
}
