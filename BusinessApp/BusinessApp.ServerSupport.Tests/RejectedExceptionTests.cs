namespace BusinessApp.ServerSupport.Tests;

/// <summary>
/// 差し戻しの例外の基底。<b>文言と内側の例外をそのまま運ぶ</b>だけで、加工しない（加工は派生が見出しを付けて行う）。
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
        Assert.Same(inner, rejected.InnerException);
        Assert.Null(new SampleRejectedException("登録できません。").InnerException);
    }
}
