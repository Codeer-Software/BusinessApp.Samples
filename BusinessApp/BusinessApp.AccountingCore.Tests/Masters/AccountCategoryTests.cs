namespace BusinessApp.AccountingCore.Tests.Masters;

using BusinessApp.AccountingCore.Masters;

/// <summary>
/// 科目区分（docs/04 §6）。<b>部門の要否と決算振替がここに依存する</b>ので、
/// 区分の判定を取り違えると部門別損益が静かに壊れる。
/// </summary>
public class AccountCategoryTests
{
    [Theory]
    [InlineData(AccountCategory.Revenue, true)]
    [InlineData(AccountCategory.Expense, true)]
    [InlineData(AccountCategory.Asset, false)]
    [InlineData(AccountCategory.Liability, false)]
    [InlineData(AccountCategory.Equity, false)]
    public void 損益科目を判定できる(AccountCategory category, bool expected)
    {
        Assert.Equal(expected, category.IsProfitAndLoss());
    }

    [Theory]
    [InlineData(AccountCategory.Asset, true)]
    [InlineData(AccountCategory.Liability, true)]
    [InlineData(AccountCategory.Equity, true)]
    [InlineData(AccountCategory.Revenue, false)]
    [InlineData(AccountCategory.Expense, false)]
    public void 貸借科目を判定できる(AccountCategory category, bool expected)
    {
        Assert.Equal(expected, category.IsBalanceSheet());
    }

    [Fact]
    public void 損益科目と貸借科目は排他で網羅している()
    {
        foreach (var category in Enum.GetValues<AccountCategory>())
        {
            Assert.NotEqual(category.IsProfitAndLoss(), category.IsBalanceSheet());
        }
    }

    [Theory]
    [InlineData(AccountCategory.Asset, DebitCredit.Debit)]
    [InlineData(AccountCategory.Expense, DebitCredit.Debit)]
    [InlineData(AccountCategory.Liability, DebitCredit.Credit)]
    [InlineData(AccountCategory.Equity, DebitCredit.Credit)]
    [InlineData(AccountCategory.Revenue, DebitCredit.Credit)]
    public void 残高が増える側を返す(AccountCategory category, DebitCredit expected)
    {
        Assert.Equal(expected, category.NormalBalance());
    }

    [Fact]
    public void 未知の科目区分は受け付けない()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((AccountCategory)999).NormalBalance());
    }
}
