namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.Partners;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 仕訳伝票（docs/04 §4-1）。日付は用途ごとに別の項目で持ち、1 つに潰さない（docs/04 §2）。
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

    /// <summary>投入元の部品名。手入力は null（docs/04 §10）。</summary>
    public string? SourceComponent { get; init; }

    public string? SourceDocumentId { get; init; }

    /// <summary>外部投入の重複防止キー（I-14）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// 入力年月日。<b>利用者は変更できない</b>。
    /// 「通常の業務処理期間の経過後の入力の事実を確認できる」という優良な電子帳簿の要件
    /// （規則 5 ⑤一イ(2)）そのものであり、取引日と共用しない（docs/04 §2）。
    /// </summary>
    public required DateTimeOffset EnteredAt { get; init; }

    /// <summary>計上日時。システムが付ける。下書きでは null。</summary>
    public DateTimeOffset? PostedAt { get; init; }

    /// <summary>
    /// 計上した人（認証部品のユーザー識別子）。システムが付ける。下書きでは null。
    /// 計上済みでも null がありうる（この列より前に計上された伝票）。
    /// </summary>
    /// <remarks>
    /// 型付き識別子（ADR-0014）にしない。ユーザーは会計コアの実体ではなく（ADR-0019）、
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

    private Yen Total(DebitCredit side)
        => Lines.Where(l => l.DebitCredit == side).Select(l => l.Amount).Sum();
}
