namespace BusinessApp.AccountingCore.Tests.Shared;

using BusinessApp.AccountingCore.Shared;

/// <summary>借方貸方（docs/04 §3）。反対仕訳の生成が反転に依存する。</summary>
public class DebitCreditTests
{
    [Theory]
    [InlineData(DebitCredit.Debit, DebitCredit.Credit)]
    [InlineData(DebitCredit.Credit, DebitCredit.Debit)]
    public void 反転できる(DebitCredit side, DebitCredit expected)
    {
        Assert.Equal(expected, side.Opposite());
    }

    [Theory]
    [InlineData(DebitCredit.Debit)]
    [InlineData(DebitCredit.Credit)]
    public void 二回反転すると元に戻る(DebitCredit side)
    {
        Assert.Equal(side, side.Opposite().Opposite());
    }
}
