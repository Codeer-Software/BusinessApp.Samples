namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;

/// <summary>
/// 仕訳モジュールが出す違反コード。docs/04 §1 の不変条件番号をそのまま使う約束が崩れると、
/// 文書とコードの対応が追えなくなる。
/// </summary>
public class JournalViolationCodesTests
{
    [Fact]
    public void 不変条件のコードは番号をそのまま使う()
    {
        Assert.Equal("I-01", JournalViolationCodes.Unbalanced);
        Assert.Equal("I-03", JournalViolationCodes.PeriodNotFound);
        Assert.Equal("I-04", JournalViolationCodes.PeriodClosed);
        Assert.Equal("I-06", JournalViolationCodes.OriginalEntryMissing);
        Assert.Equal("I-13", JournalViolationCodes.DepartmentMissing);
    }

    [Fact]
    public void 不変条件に対応しないコードは接頭辞で見分けられる()
    {
        foreach (var code in new[]
                 {
                     JournalViolationCodes.NoLines,
                     JournalViolationCodes.AmountNotPositive,
                     JournalViolationCodes.DuplicateLineNo,
                     JournalViolationCodes.AccountUnknown,
                     JournalViolationCodes.AccountInactive,
                     JournalViolationCodes.TaxCategoryMissing,
                     JournalViolationCodes.TaxLineParentInvalid,
                 })
        {
            Assert.StartsWith("E-", code);
        }
    }
}
