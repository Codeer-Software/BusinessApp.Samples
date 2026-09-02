namespace BusinessApp.Partners.Server.Tests;

using BusinessApp.Partners.Server;

/// <summary>
/// 取引先の差し戻しが<b>見出しから始まる</b>こと（qa/02 R26-26・ADR-0023）。
/// </summary>
/// <remarks>
/// 見出しを<b>投げる側ではなく例外の中で</b>付けているので、
/// 関門に <c>throw</c> を 1 本足しても形が揃う。<b>ここが、その約束を表明する場所</b>である。
/// </remarks>
public class PartnerRejectedExceptionTests
{
    [Fact]
    public void 文言は見出しから始まる()
        => Assert.Equal("登録できません。取引先コードを入力してください。",
            new PartnerRejectedException("取引先コードを入力してください。").Message);
}
