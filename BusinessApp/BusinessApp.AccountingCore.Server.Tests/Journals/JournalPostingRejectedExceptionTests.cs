namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上を止めたときの伝え方。
/// </summary>
/// <remarks>
/// <b>利用者が読む唯一の文面である。</b> 1 件だけ見せると、直しては弾かれを繰り返す。
/// </remarks>
public class JournalPostingRejectedExceptionTests
{
    [Fact]
    public void 違反を全件並べる()
    {
        var error = new JournalPostingRejectedException(
        [
            new Violation("I-01", "借方合計と貸方合計が一致していない。"),
            new Violation("I-13", "部門が要る。", LineNo: 2),
        ]);

        Assert.Contains("計上できません。", error.Message, StringComparison.Ordinal);
        Assert.Contains("・借方合計と貸方合計が一致していない。", error.Message, StringComparison.Ordinal);
        Assert.Contains("・2 行目: 部門が要る。", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 警告は文面に混ぜない()
    {
        var error = new JournalPostingRejectedException(
        [
            new Violation("I-01", "止める理由。"),
            new Violation("W-01", "気にとめるだけの話。", Severity: ViolationSeverity.Warning),
        ]);

        Assert.DoesNotContain("気にとめるだけの話。", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, error.Violations.Count);
    }
}
