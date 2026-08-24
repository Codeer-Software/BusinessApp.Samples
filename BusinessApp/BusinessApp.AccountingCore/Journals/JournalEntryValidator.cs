namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 仕訳を計上できるかを検査する（docs/04 §1）。
/// </summary>
/// <remarks>
/// <para>純粋関数であり、副作用も外部依存も持たない。<b>同じ実装をクライアントの即時
/// フィードバックとサーバの関門の両方で走らせる</b>ためである（ADR-0008）。</para>
/// <para>見つかった違反を<b>すべて</b>返す。最初の 1 件で打ち切ると、利用者は直しては弾かれを繰り返す。</para>
/// <para><b>戻り値が空かどうかで判定しない。</b> 警告は返るが計上はできる。
/// 計上の可否は <see cref="ViolationEnumerableExtensions.HasError"/> で判定する。</para>
/// </remarks>
public static class JournalEntryValidator
{
    public static IReadOnlyList<Violation> ValidateForPosting(JournalEntry entry, PostingContext context)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(context);

        var violations = new List<Violation>();
        ValidateState(entry, violations);
        ValidateStructure(entry, violations);
        ValidateDates(entry, context.Calendar, violations);
        ValidateLines(entry, context, violations);
        return violations;
    }

    /// <summary>
    /// 計上できる状態か（I-05）。計上済みの伝票をもう一度通せると、
    /// 呼び出し側が「検証が通った＝保存してよい」と解釈して二重計上の経路になる。
    /// </summary>
    private static void ValidateState(JournalEntry entry, List<Violation> violations)
    {
        if (entry.Status == EntryStatus.Posted)
        {
            violations.Add(new Violation(
                JournalViolationCodes.AlreadyPosted,
                "計上済みの仕訳は、もう一度計上できない。訂正・取消は反対仕訳で行う。"));
        }

        if (entry.EntryNo is not null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.EntryNoNotAllowed,
                "伝票番号は計上時にシステムが採番する。計上前の伝票が番号を持ってはいけない。"));
        }
    }

    private static void ValidateStructure(JournalEntry entry, List<Violation> violations)
    {
        if (entry.Lines.Count == 0)
        {
            violations.Add(new Violation(JournalViolationCodes.NoLines, "明細が 1 行もない。"));
            return;
        }

        // 借方だけ・貸方だけの伝票は、金額が正であること（E-AMOUNT）と
        // 貸借一致（I-01）の組み合わせで塞がっている。意図はテストで固定してある。
        if (!entry.IsBalanced)
        {
            violations.Add(new Violation(
                JournalViolationCodes.Unbalanced,
                $"借方合計 {entry.DebitTotal} 円と貸方合計 {entry.CreditTotal} 円が一致していない。"));
        }

        foreach (var lineNo in entry.Lines.GroupBy(l => l.LineNo).Where(g => g.Count() > 1).Select(g => g.Key))
        {
            violations.Add(new Violation(JournalViolationCodes.LineNoInvalid, "行番号が重複している。", lineNo));
        }

        foreach (var line in entry.Lines.Where(l => l.LineNo <= 0))
        {
            violations.Add(new Violation(
                JournalViolationCodes.LineNoInvalid, "行番号は正の整数でなければならない。", line.LineNo));
        }

        if (entry.EntryType.RequiresOriginalEntry() && entry.OriginalEntryId is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.OriginalEntryMissing,
                "訂正・取消の仕訳には、原仕訳の指定が要る。"));
        }
    }

    private static void ValidateDates(JournalEntry entry, FiscalCalendar calendar, List<Violation> violations)
    {
        // 取引が起きる前に帳簿へ載せることはできない。
        // 逆に取引日が過年度であることは正常なので検査しない（遅れて起票した取引を当期に計上する）。
        if (entry.PostingDate < entry.TransactionDate)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PostingDateBeforeTransaction,
                $"計上日 {entry.PostingDate:yyyy-MM-dd} が取引日 {entry.TransactionDate:yyyy-MM-dd} より前になっている。"));
        }

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
            violations.Add(new Violation(
                JournalViolationCodes.PeriodOrphaned,
                $"会計期間 {period.Period} が属する会計年度がない。マスタが壊れている。"));
            return;
        }

        if (fiscalYear.Status == PeriodStatus.Closed)
        {
            violations.Add(new Violation(
                JournalViolationCodes.PeriodClosed,
                $"会計年度「{fiscalYear.Label}」は締め済みで、計上できない。"));
            return;
        }

        // 伝票が持つ会計年度と、計上日から引いた会計年度が違うと、
        // 別の年度の番号列から伝票番号が出る（I-17 の一連番号が壊れる）。
        if (entry.FiscalYearId != period.FiscalYearId)
        {
            violations.Add(new Violation(
                JournalViolationCodes.FiscalYearMismatch,
                $"伝票の会計年度が、計上日 {entry.PostingDate:yyyy-MM-dd} の属する「{fiscalYear.Label}」と食い違っている。"));
        }
    }

    private static void ValidateLines(JournalEntry entry, PostingContext context, List<Violation> violations)
    {
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

            ValidateTaxLine(line, entry, violations);
            ValidateDepartment(line, context.Departments, violations);

            var account = context.Accounts.Find(line.AccountId);
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

            ValidateSubAccount(line, account, context.SubAccounts, violations);
        }
    }

    private static void ValidateDepartment(
        JournalLine line, DepartmentCatalog departments, List<Violation> violations)
    {
        if (line.DepartmentId is not { } departmentId)
        {
            return;
        }

        var department = departments.Find(departmentId);
        if (department is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.DepartmentUnknown,
                $"部門 {departmentId.Value} がマスタにない。",
                line.LineNo));
            return;
        }

        if (!department.IsActive)
        {
            violations.Add(new Violation(
                JournalViolationCodes.DepartmentInactive,
                $"部門「{department.Name}」は無効で、新たな計上には使えない。",
                line.LineNo));
        }
    }

    private static void ValidateSubAccount(
        JournalLine line, AccountDefinition account, SubAccountCatalog subAccounts, List<Violation> violations)
    {
        if (line.SubAccountId is not { } subAccountId)
        {
            if (account.RequiresSubAccount)
            {
                violations.Add(new Violation(
                    JournalViolationCodes.SubAccountRequired,
                    $"勘定科目「{account.Name}」は補助科目を使う。補助科目が要る。",
                    line.LineNo));
            }
            return;
        }

        var subAccount = subAccounts.Find(subAccountId);
        if (subAccount is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountUnknown,
                $"補助科目 {subAccountId.Value} がマスタにない。",
                line.LineNo));
            return;
        }

        // 「現金の補助科目を普通預金の明細に付ける」を塞ぐ。
        // 補助元帳は補助科目が正しい親に属していることを前提にする。
        if (subAccount.AccountId != account.Id)
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountMismatch,
                $"補助科目「{subAccount.Name}」は勘定科目「{account.Name}」に属していない。",
                line.LineNo));
            return;
        }

        if (!subAccount.IsActive)
        {
            violations.Add(new Violation(
                JournalViolationCodes.SubAccountInactive,
                $"補助科目「{subAccount.Name}」は無効で、新たな計上には使えない。",
                line.LineNo));
        }
    }

    private static void ValidateTaxLine(JournalLine line, JournalEntry entry, List<Violation> violations)
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

        var parent = entry.Lines.FirstOrDefault(l => l.LineNo == line.ParentLineNo);
        if (parent is null)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineParentInvalid,
                "消費税行は、伝票内に存在する本体行を指していなければならない。",
                line.LineNo));
            return;
        }

        if (parent.IsTaxLine)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineParentInvalid,
                "消費税行が別の消費税行を親に指している。",
                line.LineNo));
            return;
        }

        // 消費税行は本体行から貸借・部門・税区分・用途区分を引き継ぐ（docs/06 §2）。
        // 引き継がないと、税区分別集計・部門別税集計が本体行と突き合わなくなる。
        if (line.DebitCredit != parent.DebitCredit)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の貸借は、本体行と同じでなければならない。",
                line.LineNo));
        }

        if (line.DepartmentId != parent.DepartmentId)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の部門は、本体行から引き継がなければならない。",
                line.LineNo));
        }

        if (line.TaxCategoryId != parent.TaxCategoryId)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の税区分は、本体行から引き継がなければならない。",
                line.LineNo));
        }

        if (line.TaxTreatment != parent.TaxTreatment)
        {
            violations.Add(new Violation(
                JournalViolationCodes.TaxLineNotInherited,
                "消費税行の用途区分は、本体行から引き継がなければならない。",
                line.LineNo));
        }
    }
}
