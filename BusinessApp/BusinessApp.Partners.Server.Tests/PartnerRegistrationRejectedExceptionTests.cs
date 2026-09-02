namespace BusinessApp.Partners.Server.Tests;

using BusinessApp.Partners.Server;

/// <summary>
/// 登録番号の差し戻しが<b>見出しから始まる</b>こと（qa/02 R26-26・ADR-0023）。
/// </summary>
public class PartnerRegistrationRejectedExceptionTests
{
    [Fact]
    public void 文言は見出しから始まる()
        => Assert.Equal("登録できません。登録年月日を入力してください。",
            new PartnerRegistrationRejectedException("登録年月日を入力してください。").Message);
}
