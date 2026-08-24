namespace BusinessApp.AccountingCore.Tests.Accounts;

using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Shared;

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
    [InlineData(AccountCategory.Asset, DebitCredit.Debit)]
    [InlineData(AccountCategory.Expense, DebitCredit.Debit)]
    [InlineData(AccountCategory.Liability, DebitCredit.Credit)]
    [InlineData(AccountCategory.Equity, DebitCredit.Credit)]
    [InlineData(AccountCategory.Revenue, DebitCredit.Credit)]
    public void 残高が増える側を返す(AccountCategory category, DebitCredit expected)
    {
        Assert.Equal(expected, category.NormalBalance());
    }

    /// <summary>
    /// 評価勘定は通常残高が科目区分と逆になる。科目区分だけから貸借を決めると必ず誤る。
    /// </summary>
    [Theory]
    [InlineData(AccountCategory.Asset, false, DebitCredit.Debit)]
    [InlineData(AccountCategory.Asset, true, DebitCredit.Credit)]    // 減価償却累計額・貸倒引当金
    [InlineData(AccountCategory.Revenue, false, DebitCredit.Credit)]
    [InlineData(AccountCategory.Revenue, true, DebitCredit.Debit)]   // 売上値引・戻り高
    [InlineData(AccountCategory.Expense, true, DebitCredit.Credit)]  // 期末仕掛品棚卸高
    public void 評価勘定の通常残高は科目区分と逆になる(AccountCategory category, bool isContra, DebitCredit expected)
    {
        var account = new AccountDefinition(new AccountId(1), "9999", "テスト", category, IsContra: isContra);

        Assert.Equal(expected, account.NormalBalance);
    }

    [Fact]
    public void 未知の科目区分は受け付けない()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((AccountCategory)999).NormalBalance());
    }
}
