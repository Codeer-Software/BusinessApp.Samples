namespace BusinessApp.AccountingCore.Accounts;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 科目区分（docs/10 §6）。決算書表示区分とは別物であり、
/// <b>部門の要否判定と決算振替はこちらに依存する</b>。
/// </summary>
public enum AccountCategory
{
    /// <summary>資産。</summary>
    Asset,

    /// <summary>負債。</summary>
    Liability,

    /// <summary>純資産。</summary>
    Equity,

    /// <summary>収益。</summary>
    Revenue,

    /// <summary>費用。</summary>
    Expense,
}

public static class AccountCategoryExtensions
{
    /// <summary>損益科目（収益・費用）か。部門の要否（I-13）と決算振替の対象判定に使う。</summary>
    public static bool IsProfitAndLoss(this AccountCategory category)
        => category is AccountCategory.Revenue or AccountCategory.Expense;

    /// <summary>その科目区分の残高が増える側。借方残の科目（資産・費用）は借方。</summary>
    public static DebitCredit NormalBalance(this AccountCategory category) => category switch
    {
        AccountCategory.Asset or AccountCategory.Expense => DebitCredit.Debit,
        AccountCategory.Liability or AccountCategory.Equity or AccountCategory.Revenue => DebitCredit.Credit,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "未知の科目区分"),
    };
}
