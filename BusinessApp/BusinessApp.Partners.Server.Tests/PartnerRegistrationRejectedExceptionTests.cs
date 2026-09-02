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

    [Fact]
    public void 理由は見出しを含まない()
        => Assert.Equal("登録年月日を入力してください。",
            new PartnerRegistrationRejectedException("登録年月日を入力してください。").Reason);

    /// <summary>トースト内の文字列は改行できない（qa/01 D-12）。</summary>
    [Fact]
    public void 見出しの付け方で改行が混ざらない()
        => Assert.DoesNotContain(
            "\n", new PartnerRegistrationRejectedException("理由。").Message, StringComparison.Ordinal);
}
