namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;

/// <summary>
/// 明細の 1 行だけで決まる規則（保存の手前の網と、計上の検証が共有する）。
/// </summary>
/// <remarks>
/// <b>境界は DDL に合わせてある。</b> ここが DDL より広いと、関門が
/// 「DB に拒まれる値」を保存へ渡してしまう（qa/03 L-14 の型）。狭いと、
/// 正しい値を持つ利用者だけが計上できなくなる。<b>両側を表明する。</b>
/// </remarks>
public class JournalLineRulesTests
{
    /// <summary>
    /// <c>journal_lines.amount</c> は <c>CHECK (amount &gt; 0 AND typeof(amount) = 'integer')</c>。
    /// SQLite の INTEGER は 64 ビットなので、<see cref="long"/> が上限そのものである。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1000000)]
    public void 金額は一円以上の整数を通す(long amount)
    {
        Assert.True(JournalLineRules.IsStorableAmount(amount));
    }

    [Fact]
    public void 金額は上限ちょうどを通し_その一つ上を拒む()
    {
        Assert.True(JournalLineRules.IsStorableAmount(long.MaxValue));
        Assert.False(JournalLineRules.IsStorableAmount((decimal)long.MaxValue + 1));
    }

    /// <remarks>
    /// <c>decimal</c> は属性の引数にできないので <c>double</c> で受けて変換する。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.5)]
    [InlineData(1.5)]
    [InlineData(-0.5)]
    public void 金額は正でない値と端数のある値を拒む(double amount)
    {
        Assert.False(JournalLineRules.IsStorableAmount((decimal)amount));
    }

    /// <summary>
    /// 行番号の上限は DB ではなく<b>こちらの都合</b>（差し戻しに添える <c>int?</c> に収める）。
    /// </summary>
    [Fact]
    public void 行番号は_int_の上限ちょうどを通し_その一つ上を拒む()
    {
        Assert.True(JournalLineRules.IsStorableLineNo(int.MaxValue));
        Assert.False(JournalLineRules.IsStorableLineNo((decimal)int.MaxValue + 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void 行番号は一以上の整数を通す(int lineNo)
    {
        Assert.True(JournalLineRules.IsStorableLineNo(lineNo));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public void 行番号は正でない値と端数のある値を拒む(double lineNo)
    {
        Assert.False(JournalLineRules.IsStorableLineNo((decimal)lineNo));
    }

    /// <summary>
    /// <b>文言をここが持つ理由を、テストでも固定する。</b> 同じ原因に 2 通りの言い方があると、
    /// どちらの層が先に捕まえたかで利用者に出る言葉が変わる。
    /// </summary>
    [Fact]
    public void 差し戻しの文言に改行を入れない()
    {
        string[] messages =
        [
            JournalLineRules.AmountNotPositive, JournalLineRules.AmountHasFraction,
            JournalLineRules.AmountTooLarge, JournalLineRules.LineNoNotStorable,
            JournalLineRules.TaxCategoryMissing, JournalLineRules.LineNoMissing,
            JournalLineRules.DebitCreditMissing, JournalLineRules.AccountMissing,
            JournalLineRules.AmountMissing, JournalLineRules.TransactionDateMissing,
            JournalLineRules.PostingDateMissing, JournalLineRules.FiscalYearMissing,
        ];

        // トースト内の文字列は改行できない（qa/01 D-12）。
        Assert.All(messages, m => Assert.DoesNotContain("\n", m, StringComparison.Ordinal));

        // です・ます調（docs/09 §2-1）。
        Assert.All(messages, m => Assert.EndsWith("。", m, StringComparison.Ordinal));
    }
}
