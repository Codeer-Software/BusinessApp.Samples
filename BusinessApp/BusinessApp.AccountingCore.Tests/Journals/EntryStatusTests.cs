namespace BusinessApp.AccountingCore.Tests.Journals;

using BusinessApp.AccountingCore.Journals;

/// <summary>仕訳の状態（docs/04 §5）。</summary>
public class EntryStatusTests
{
    [Theory]
    [InlineData(EntryStatus.Draft, "下書き")]
    [InlineData(EntryStatus.Posted, "計上済み")]
    public void 利用者に見せる名前を持つ(EntryStatus status, string expected)
    {
        Assert.Equal(expected, status.DisplayName());
    }

    [Fact]
    public void 知らない状態の名前は求められない()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((EntryStatus)99).DisplayName());
    }
}
