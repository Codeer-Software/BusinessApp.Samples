namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 伝票番号の採番（I-17）。**進めるだけで戻さない**ことが不変条件である。
/// </summary>
public class EntryNumberSequenceTests
{
    [Fact]
    public void 会計年度の採番は一番から始まる()
    {
        Assert.Equal(1, EntryNumberSequence.StartOf(AccountingFixture.FiscalYear).NextValue);
    }

    [Fact]
    public void 払い出すたびに次の番号へ進む()
    {
        var sequence = EntryNumberSequence.StartOf(AccountingFixture.FiscalYear);

        var (first, afterFirst) = sequence.Allocate();
        var (second, afterSecond) = afterFirst.Allocate();

        Assert.Equal(1, first.Value);
        Assert.Equal(2, second.Value);
        Assert.Equal(3, afterSecond.NextValue);
    }

    [Fact]
    public void 払い出しても元の状態は変わらない()
    {
        // 値型なので、採番の状態を書き戻し忘れると同じ番号が二度出る。
        // 「元が変わらない」ことを固定して、書き戻しが必要だと分かるようにする。
        var sequence = EntryNumberSequence.StartOf(AccountingFixture.FiscalYear);

        sequence.Allocate();

        Assert.Equal(1, sequence.NextValue);
    }

    [Fact]
    public void 番号は会計年度を伴う()
    {
        var (number, _) = EntryNumberSequence.StartOf(AccountingFixture.FiscalYear).Allocate();

        Assert.Equal(AccountingFixture.FiscalYear, number.FiscalYearId);
    }

    [Fact]
    public void 壊れた採番からは払い出せない()
    {
        var broken = new EntryNumberSequence(AccountingFixture.FiscalYear, 0);

        Assert.Throws<InvalidOperationException>(() => broken.Allocate());
    }

    [Fact]
    public void 表記はゼロ埋めした六桁になる()
    {
        var (number, _) = new EntryNumberSequence(AccountingFixture.FiscalYear, 42).Allocate();

        Assert.Equal("000042", number.ToString());
    }
}
