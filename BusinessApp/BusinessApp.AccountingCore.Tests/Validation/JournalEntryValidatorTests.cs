namespace BusinessApp.AccountingCore.Tests.Validation;

using BusinessApp.AccountingCore.Calendar;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Masters;
using BusinessApp.AccountingCore.Primitives;
using BusinessApp.AccountingCore.Tests.Fixtures;
using BusinessApp.AccountingCore.Validation;

/// <summary>計上の関門（docs/04 §1 の不変条件）。</summary>
public class JournalEntryValidatorTests
{
    private static readonly DateOnly Ordinary = new(2026, 5, 20);

    [Fact]
    public void 貸借が一致した仕訳は計上できる()
    {
        Assert.Empty(Validate(AccountingFixture.CashSale(Ordinary)));
    }

    [Fact]
    public void 貸借が一致しない仕訳は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 99_999,
                department: AccountingFixture.SalesDepartment));

        AssertViolation(ViolationCodes.Unbalanced, Validate(entry));
    }

    [Fact]
    public void 明細のない仕訳は計上できない()
    {
        var violations = Validate(AccountingFixture.Entry(Ordinary));

        AssertViolation(ViolationCodes.NoLines, violations);
        // 明細が無い時点で以降の明細検査は無意味なので、貸借一致の違反は重ねて出さない。
        Assert.DoesNotContain(violations, v => v.Code == ViolationCodes.Unbalanced);
    }

    [Fact]
    public void 会計期間のない日付には計上できない()
    {
        AssertViolation(ViolationCodes.PeriodNotFound, Validate(AccountingFixture.CashSale(new DateOnly(2027, 4, 1))));
    }

    [Fact]
    public void 締め済みの期間には計上できない()
    {
        var entry = AccountingFixture.CashSale(new DateOnly(2026, 9, 15));
        var context = AccountingFixture.Context(septemberStatus: PeriodStatus.Closed);

        AssertViolation(ViolationCodes.PeriodClosed, JournalEntryValidator.ValidateForPosting(entry, context));
    }

    [Fact]
    public void 締め済みの年度には計上できない()
    {
        var entry = AccountingFixture.CashSale(Ordinary);
        var context = AccountingFixture.Context(fiscalYearStatus: PeriodStatus.Closed);

        AssertViolation(ViolationCodes.PeriodClosed, JournalEntryValidator.ValidateForPosting(entry, context));
    }

    [Fact]
    public void 期間が属する会計年度がなければ計上できない()
    {
        // 期間はあるのに年度が無いのはマスタが壊れた状態。
        // FiscalCalendar.IsPostable と判断が食い違わないことを固定する。
        var orphan = new AccountingPeriod(
            "ORPHAN",
            "FY99",
            new EffectivePeriod(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31)),
            PeriodStatus.Open);
        var context = new PostingContext(
            new AccountCatalog(AccountingFixture.Accounts),
            new FiscalCalendar([], [orphan]));

        var violations = JournalEntryValidator.ValidateForPosting(AccountingFixture.CashSale(Ordinary), context);

        Assert.Contains(violations, v => v.Code == ViolationCodes.PeriodNotFound);
        Assert.False(context.Calendar.IsPostable(Ordinary));
    }

    [Fact]
    public void 損益科目の明細に部門がなければ計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 100_000));

        Assert.Equal(2, AssertViolation(ViolationCodes.DepartmentMissing, Validate(entry)).LineNo);
    }

    [Fact]
    public void 貸借科目の明細に部門はなくてよい()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 50_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 50_000));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 金額が零以下の明細は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 0),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 0));

        AssertViolation(ViolationCodes.AmountNotPositive, Validate(entry));
    }

    [Fact]
    public void 税区分のない明細は計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000, taxCategoryId: ""),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(ViolationCodes.TaxCategoryMissing, Validate(entry));
    }

    [Fact]
    public void 無効な勘定科目は新たに使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.RetiredExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        AssertViolation(ViolationCodes.AccountInactive, Validate(entry));
    }

    [Fact]
    public void マスタにない勘定科目は使えない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, "9999", 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Cash, 1_000));

        var violations = Validate(entry);

        AssertViolation(ViolationCodes.AccountUnknown, violations);
        // 科目が引けない行に「部門がない」まで重ねて出さない。
        Assert.DoesNotContain(violations, v => v.Code == ViolationCodes.DepartmentMissing);
    }

    [Fact]
    public void 行番号は重複できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(1, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        Assert.Equal(1, AssertViolation(ViolationCodes.DuplicateLineNo, Validate(entry)).LineNo);
    }

    [Theory]
    [InlineData(EntryType.Correction)]
    [InlineData(EntryType.Reversal)]
    public void 訂正と取消は原仕訳の指定を要求する(EntryType entryType)
    {
        var entry = AccountingFixture.CashSale(Ordinary) with { EntryType = entryType };

        AssertViolation(ViolationCodes.OriginalEntryMissing, Validate(entry));
    }

    [Fact]
    public void 原仕訳を指定した取消は計上できる()
    {
        var entry = AccountingFixture.CashSale(Ordinary) with
        {
            EntryType = EntryType.Reversal,
            OriginalEntryId = "JE-0001",
        };

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 消費税行は伝票内の行を指していなければならない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000) with
            {
                IsTaxLine = true,
                ParentLineNo = 99,
            });

        AssertViolation(ViolationCodes.TaxLineParentInvalid, Validate(entry));
    }

    [Fact]
    public void 消費税行は親行の指定を省略できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000) with
            {
                IsTaxLine = true,
            });

        AssertViolation(ViolationCodes.TaxLineParentInvalid, Validate(entry));
    }

    [Fact]
    public void 消費税行が別の消費税行を親に指すことはできない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 100) with
            {
                IsTaxLine = true,
                ParentLineNo = 1,
            },
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_100) with
            {
                IsTaxLine = true,
                ParentLineNo = 2,
            });

        Assert.Equal(3, AssertViolation(ViolationCodes.TaxLineParentInvalid, Validate(entry)).LineNo);
    }

    [Fact]
    public void 親行を正しく指す消費税行は計上できる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 1_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 100) with
            {
                IsTaxLine = true,
                ParentLineNo = 1,
            },
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_100));

        Assert.Empty(Validate(entry));
    }

    [Fact]
    public void 本体行に親行は指定できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000) with { ParentLineNo = 2 },
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(ViolationCodes.TaxLineParentInvalid, Validate(entry));
    }

    [Fact]
    public void 違反は一件で打ち切らずすべて返す()
    {
        var entry = AccountingFixture.Entry(
            new DateOnly(2027, 4, 1),
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 999));

        var codes = Validate(entry).Select(v => v.Code).ToList();

        Assert.Contains(ViolationCodes.Unbalanced, codes);
        Assert.Contains(ViolationCodes.PeriodNotFound, codes);
        Assert.Contains(ViolationCodes.DepartmentMissing, codes);
    }

    [Fact]
    public void nullでは検証できない()
    {
        Assert.Throws<ArgumentNullException>(
            () => JournalEntryValidator.ValidateForPosting(null!, AccountingFixture.Context()));
        Assert.Throws<ArgumentNullException>(
            () => JournalEntryValidator.ValidateForPosting(AccountingFixture.CashSale(Ordinary), null!));
    }

    private static IReadOnlyList<Violation> Validate(JournalEntry entry)
        => JournalEntryValidator.ValidateForPosting(entry, AccountingFixture.Context());

    private static Violation AssertViolation(string code, IReadOnlyList<Violation> violations)
    {
        var violation = violations.FirstOrDefault(v => v.Code == code);
        Assert.True(violation is not null, $"{code} が検出されていない。実際: {string.Join(" / ", violations)}");
        return violation!;
    }
}
