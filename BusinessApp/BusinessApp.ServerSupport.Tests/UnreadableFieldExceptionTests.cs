namespace BusinessApp.ServerSupport.Tests;

using BusinessApp.ServerSupport;

/// <summary>
/// 差分に載っているのに読めない型で届いた欄（関門の共通の合図）。
/// </summary>
/// <remarks>
/// <b>これは利用者の誤りではない</b>ので、文言はホストが定型文へ差し替え、中身はログへ回す
/// （<c>AccountingSubmitPipeline</c>）。ここが見るのは<b>合図そのもの</b>である。
/// </remarks>
public class UnreadableFieldExceptionTests
{
    [Fact]
    public void モジュールと欄の名前と型名を持つ()
    {
        var thrown = UnreadableFieldException.For("Account", "Code", new NumberBox());

        Assert.Equal("Account", thrown.Module);
        Assert.Equal("Code", thrown.Field);
        Assert.Equal("NumberBox", thrown.TypeName);
    }

    /// <summary>
    /// <b>値が <c>null</c> でも落ちない。</b>
    /// </summary>
    /// <remarks>
    /// 入口はクライアント由来のデシリアライズなので <c>null</c> が届きうる。
    /// ここで <c>NullReferenceException</c> になると<b>定型文へ差し替える口を通らず</b>、
    /// この型を作った意味が消える（2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public void 値が_null_でも型名を作れる()
    {
        var thrown = UnreadableFieldException.For("Account", "Code", null);

        Assert.Equal("null", thrown.TypeName);
    }

    /// <summary>
    /// 文言はモジュール・欄の名前・型名を全部含む。
    /// </summary>
    /// <remarks>
    /// <b>欄の名前だけでは直す先が決まらない</b>——<c>Code</c> は 6 つのモジュールにある。
    /// </remarks>
    [Fact]
    public void 文言はモジュールと欄の名前と型名を含む()
    {
        var thrown = new UnreadableFieldException("Department", "IsCompanyWide", "TextFieldData");

        Assert.Contains("Department", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("IsCompanyWide", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("TextFieldData", thrown.Message, StringComparison.Ordinal);
    }

    private sealed class NumberBox;
}
