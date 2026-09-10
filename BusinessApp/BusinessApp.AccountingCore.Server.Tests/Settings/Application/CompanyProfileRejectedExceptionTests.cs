namespace BusinessApp.AccountingCore.Server.Tests.Settings.Application;

using BusinessApp.AccountingCore.Server.Settings.Application;

/// <summary>
/// 自社情報の差し戻しが<b>見出しから始まる</b>こと（qa/02 R26-26・ADR-0023）。
/// </summary>
/// <remarks>
/// <b>見出しは押したボタンで決まる</b>（qa/02 R24-23）。自社情報の画面のボタンは「保存」なので、
/// 仕訳の「計上できません」ではなく「保存できません」と断る。
/// </remarks>
public class CompanyProfileRejectedExceptionTests
{
    [Fact]
    public void 文言は見出しから始まる()
        => Assert.Equal("保存できません。会社名を入力してください。",
            new CompanyProfileRejectedException("会社名を入力してください。").Message);
}
