namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>伝票の集計（I-01）。金額は正で持ち、向きは借方貸方が表す。</summary>
public class JournalEntryTests
{
    private static readonly DateOnly Ordinary = new(2026, 5, 20);

    [Fact]
    public void 借方と貸方を別々に合計する()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.SuppliesExpense, 30_000,
                department: AccountingFixture.SalesDepartment),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 20_000),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.AccountsPayable, 50_000));

        Assert.Equal(Yen.From(50_000), entry.DebitTotal);
        Assert.Equal(Yen.From(50_000), entry.CreditTotal);
        Assert.True(entry.IsBalanced);
    }

    [Fact]
    public void 貸借がずれていれば不一致になる()
    {
        var entry = AccountingFixture.Entry(
            Ordinary,
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 50_000),
            AccountingFixture.Line(2, DebitCredit.Credit, AccountingFixture.AccountsPayable, 49_999));

        Assert.False(entry.IsBalanced);
        Assert.Equal(Yen.From(1), entry.DebitTotal - entry.CreditTotal);
    }

    [Fact]
    public void 明細のない伝票は貸借零で一致する()
    {
        var entry = AccountingFixture.Entry(Ordinary);

        Assert.Equal(Yen.Zero, entry.DebitTotal);
        Assert.Equal(Yen.Zero, entry.CreditTotal);
        Assert.True(entry.IsBalanced);
    }

    [Fact]
    public void 明細の順序を変えても合計は変わらない()
    {
        var lines = new[]
        {
            AccountingFixture.Line(1, DebitCredit.Debit, AccountingFixture.Cash, 10_000),
            AccountingFixture.Line(2, DebitCredit.Debit, AccountingFixture.Cash, 20_000),
            AccountingFixture.Line(3, DebitCredit.Credit, AccountingFixture.AccountsPayable, 30_000),
        };

        var forward = AccountingFixture.Entry(Ordinary, lines);
        var reversed = AccountingFixture.Entry(Ordinary, lines.Reverse().ToArray());

        Assert.Equal(forward.DebitTotal, reversed.DebitTotal);
        Assert.Equal(forward.CreditTotal, reversed.CreditTotal);
    }
}
