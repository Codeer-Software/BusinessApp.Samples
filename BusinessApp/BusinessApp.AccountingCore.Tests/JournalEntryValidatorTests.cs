namespace BusinessApp.AccountingCore.Tests;

using BusinessApp.AccountingCore.Calendar;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Masters;
using BusinessApp.AccountingCore.Tests.Fixtures;
using BusinessApp.AccountingCore.Validation;

/// <summary>計上の関門（docs/04 §1 の不変条件）。</summary>
public class JournalEntryValidatorTests
{
    private static readonly DateOnly Ordinary = new(2026, 5, 20);

    [Fact]
    public void 貸借が一致した仕訳は計上できる()
    {
        var violations = Validate(AccountingFixture.CashSale(Ordinary));

        Assert.Empty(violations);
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
        AssertViolation(ViolationCodes.NoLines, Validate(AccountingFixture.Entry(Ordinary)));
    }

    [Fact]
    public void 会計期間のない日付には計上できない()
    {
        var entry = AccountingFixture.CashSale(new DateOnly(2027, 4, 1));

        AssertViolation(ViolationCodes.PeriodNotFound, Validate(entry));
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
    public void 損益科目の明細に部門がなければ計上できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 100_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.Sales, 100_000));

        var violation = AssertViolation(ViolationCodes.DepartmentMissing, Validate(entry));
        Assert.Equal(2, violation.LineNo);
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

        AssertViolation(ViolationCodes.AccountUnknown, Validate(entry));
    }

    [Fact]
    public void 行番号は重複できない()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 1_000),
            AccountingFixture.Line(1, DebitCredit.Credit, AccountingFixture.AccountsPayable, 1_000));

        AssertViolation(ViolationCodes.DuplicateLineNo, Validate(entry));
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
    public void 消費税行は伝票内の本体行を指していなければならない()
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

    private static IReadOnlyList<Violation> Validate(JournalEntry entry)
        => JournalEntryValidator.ValidateForPosting(entry, AccountingFixture.Context());

    private static Violation AssertViolation(string code, IReadOnlyList<Violation> violations)
    {
        var violation = violations.FirstOrDefault(v => v.Code == code);
        Assert.True(violation is not null, $"{code} が検出されていない。実際: {string.Join(" / ", violations)}");
        return violation!;
    }
}
