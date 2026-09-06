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

    [Theory]
    [InlineData(EntryType.Normal, true)]
    [InlineData(EntryType.Correction, true)]
    [InlineData(EntryType.Reversal, false)]
    [InlineData(EntryType.Opening, false)]
    [InlineData(EntryType.Closing, false)]
    [InlineData(EntryType.Carryover, false)]
    public void 取消訂正の対象にできる種別を判定できる(EntryType type, bool expected)
    {
        // 訂正が対象に入るのは、訂正を間違えたときに詰まないため（ADR-0015）。
        // 取消が外れるのは、取消の取消が何も表現しないため。
        Assert.Equal(expected, type.IsAmendable());
    }

    [Theory]
    [InlineData(EntryType.Normal, "通常")]
    [InlineData(EntryType.Correction, "訂正")]
    [InlineData(EntryType.Reversal, "取消")]
    [InlineData(EntryType.Opening, "期首残高")]
    [InlineData(EntryType.Closing, "決算振替")]
    [InlineData(EntryType.Carryover, "繰越")]
    public void 利用者に見せる名前を持つ(EntryType type, string expected)
    {
        // 列挙子をそのまま文言に混ぜると、画面に `Reversal` と出る（docs/21_画面の原則.md §2）。
        // CLB のデザイン enum と一致することは EnumConsistencyTests が別に見る。
        Assert.Equal(expected, type.DisplayName());
    }

    [Fact]
    public void 知らない種別の名前は求められない()
    {
        // 列挙子を足して表示名を足し忘れたら、その場で止まる。
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EntryType)99).DisplayName());
    }
}
