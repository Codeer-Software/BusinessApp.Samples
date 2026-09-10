namespace BusinessApp.ServerSupport.Tests;

/// <summary>
/// 差し戻しの例外の基底。<b>文言と内側の例外をそのまま運ぶ</b>だけで、加工しない（加工は派生が見出しを付けて行う）。
/// <b>内側の例外の文言は <c>Message</c> に混ぜない</b>——ホストの例外ハンドラは <c>InnerException</c> まで連ねて出すので、
/// 混ぜると同じ文が 2 回出る。
/// </summary>
public class RejectedExceptionTests
{
    private sealed class SampleRejectedException(string message, Exception? inner = null) : RejectedException(message, inner);

    [Fact]
    public void 文言と内側の例外をそのまま運ぶ()
    {
        var inner = new InvalidOperationException("内側");

        var rejected = new SampleRejectedException("登録できません。理由。", inner);

        Assert.Equal("登録できません。理由。", rejected.Message);
        Assert.DoesNotContain("内側", rejected.Message, StringComparison.Ordinal);
        Assert.Same(inner, rejected.InnerException);
        Assert.Null(new SampleRejectedException("登録できません。").InnerException);
    }
}
