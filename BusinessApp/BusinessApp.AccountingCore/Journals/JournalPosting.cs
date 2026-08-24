namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 下書きを計上する（docs/04 §5）。検証 → 採番 → 状態遷移を 1 か所に集める。
/// </summary>
/// <remarks>
/// <para>純粋関数である。DB も時計も知らず、採番の状態と現在時刻は呼び出し側が渡す
/// （ADR-0008・ADR-0012）。サーバはこの結果を 1 つのトランザクションで書き込む。</para>
/// <para><b>ここを通らない計上経路を作らない。</b> 検証を通さずに status を posted にできる道が
/// 1 つでもあると、ADR-0004 が最も守りたかった場所に穴が開く。</para>
/// </remarks>
public static class JournalPosting
{
    public static PostingResult Post(
        JournalEntry draft,
        PostingContext context,
        EntryNumberSequence sequence,
        DateTimeOffset postedAt)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);

        var violations = JournalEntryValidator.ValidateForPosting(draft, context).ToList();

        // 別の年度の番号列から番号を出さない（I-17 の一連番号が壊れる）。
        if (sequence.FiscalYearId != draft.FiscalYearId)
        {
            violations.Add(new Violation(
                JournalViolationCodes.FiscalYearMismatch,
                "伝票の会計年度と、採番に使う会計年度が食い違っている。"));
        }

        if (violations.HasError())
        {
            return new PostingResult(violations);
        }

        var (number, next) = sequence.Allocate();
        var posted = draft with
        {
            Status = EntryStatus.Posted,
            EntryNo = number.Value,
            PostedAt = postedAt,
        };

        return new PostingResult(violations, posted, number, next);
    }
}
