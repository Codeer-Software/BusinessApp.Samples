namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 仕訳を計上できるかを検査する（docs/04 §1）。
/// </summary>
/// <remarks>
/// <para>純粋関数であり、副作用も外部依存も持たない。<b>同じ実装をクライアントの即時
/// フィードバックとサーバの関門の両方で走らせる</b>ためである（ADR-0008）。</para>
/// <para>見つかった違反を<b>すべて</b>返す。最初の 1 件で打ち切ると、利用者は直しては弾かれを繰り返す。</para>
/// </remarks>
public static class JournalEntryValidator
{
    public static IReadOnlyList<Violation> ValidateForPosting(JournalEntry entry, PostingContext context)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(context);

        var violations = new List<Violation>();
        ValidateStructure(entry, violations);
        ValidatePeriod(entry, context.Calendar, violations);
        ValidateLines(entry, context.Accounts, violations);
        return violations;
    }

    private static void ValidateStructure(JournalEntry entry, List<Violation> violations)
    {
        if (entry.Lines.Count == 0)
        {
            violations.Add(new Violation(JournalViolationCodes.NoLines, "明細が 1 行もない。"));
            return;
        }

        if (!entry.IsBalanced)
        {
            violations.Add(new Violation(
                JournalViolationCodes.Unbalanced,
                $"借方合計 {entry.DebitTotal} 円と貸方合計 {entry.CreditTotal} 円が一致していない。"));
        }

        var duplicated = entry.Lines
            .GroupBy(l => l.LineNo)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var lineNo in duplicated)
        {
            violations.Add(new Violation(JournalViolationCodes.DuplicateLineNo, "行番号が重複している。", lineNo));
        }

        if (entry.EntryType.RequiresOriginalEntry() && entry.OriginalEntryId is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.OriginalEntryMissing,
                "訂正・取消の仕訳には、原仕訳の指定が要る。"));
        }
    }

    private static void ValidatePeriod(JournalEntry entry, FiscalCalendar calendar, List<Violation> violations)
    {
        var period = calendar.ResolvePeriod(entry.PostingDate);
        if (period is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodNotFound,
                $"計上日 {entry.PostingDate:yyyy-MM-dd} に対応する会計期間がない。"));
            return;
        }

        if (period.Status == PeriodStatus.Closed)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodClosed,
                $"会計期間 {period.Period} は締め済みで、計上できない。"));
            return;
        }

        var fiscalYear = calendar.FindFiscalYear(period.FiscalYearId);
        if (fiscalYear is null)
        {
            // 期間はあるのに年度が無い状態。マスタが壊れているので、素通しせず必ず弾く。
            violations.Add(new Violation(
                JournalViolationCodes.PeriodNotFound,
                $"会計期間 {period.Period} が属する会計年度 {period.FiscalYearId} がない。"));
            return;
        }

        if (fiscalYear.Status == PeriodStatus.Closed)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodClosed,
                $"会計年度「{fiscalYear.Label}」は締め済みで、計上できない。"));
        }
    }

    private static void ValidateLines(JournalEntry entry, IAccountLookup accounts, List<Violation> violations)
    {
        var lineNumbers = entry.Lines.Select(l => l.LineNo).ToHashSet();

        foreach (var line in entry.Lines)
        {
            if (!line.Amount.IsPositive)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AmountNotPositive,
                    "金額は正でなければならない。減額は貸借を入れ替えて表す。",
                    line.LineNo));
            }

            // 既定値のまま（未設定）の税区分を通さない。NULL と「対象外」を 2 通りで表さない（docs/06 §1）。
            if (line.TaxCategoryId == default)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.TaxCategoryMissing,
                    "税区分がない。税に意味のない行にも「対象外」を明示する。",
                    line.LineNo));
            }

            ValidateTaxLineParent(line, lineNumbers, entry, violations);

            var account = accounts.Find(line.AccountId);
            if (account is null)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AccountUnknown,
                    $"勘定科目 {line.AccountId.Value} がマスタにない。",
                    line.LineNo));
                continue;
            }

            if (!account.IsActive)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.AccountInactive,
                    $"勘定科目「{account.Name}」は無効で、新たな計上には使えない。",
                    line.LineNo));
            }

            if (account.Category.IsProfitAndLoss() && line.DepartmentId is null)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.DepartmentMissing,
                    $"損益科目「{account.Name}」の明細には部門が要る。",
                    line.LineNo));
            }
        }
    }

    private static void ValidateTaxLineParent(
        JournalLine line,
        IReadOnlySet<int> lineNumbers,
        JournalEntry entry,
        List<Violation> violations)
    {
        if (!line.IsTaxLine)
        {
            if (line.ParentLineNo is not null)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.TaxLineParentInvalid,
                    "本体行に親行を指定できない。",
                    line.LineNo));
            }
            return;
        }

        if (line.ParentLineNo is not { } parentLineNo || !lineNumbers.Contains(parentLineNo))
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineParentInvalid,
                "消費税行は、伝票内に存在する本体行を指していなければならない。",
                line.LineNo));
            return;
        }

        if (entry.Lines.Any(l => l.LineNo == parentLineNo && l.IsTaxLine))
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineParentInvalid,
                "消費税行が別の消費税行を親に指している。",
                line.LineNo));
        }
    }
}
