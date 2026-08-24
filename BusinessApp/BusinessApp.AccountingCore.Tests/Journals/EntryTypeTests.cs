namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;

/// <summary>仕訳の種別（docs/04 §4-1・§5）。</summary>
public class EntryTypeTests
{
    [Theory]
    [InlineData(EntryType.Correction, true)]
    [InlineData(EntryType.Reversal, true)]
    [InlineData(EntryType.Normal, false)]
    [InlineData(EntryType.Opening, false)]
    [InlineData(EntryType.Closing, false)]
    [InlineData(EntryType.Carryover, false)]
    public void 原仕訳の指定が要る種別を判定できる(EntryType type, bool expected)
    {
        Assert.Equal(expected, type.RequiresOriginalEntry());
    }

}
