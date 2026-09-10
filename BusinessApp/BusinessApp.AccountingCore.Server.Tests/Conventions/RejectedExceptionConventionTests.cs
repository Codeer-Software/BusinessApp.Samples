namespace BusinessApp.AccountingCore.Server.Tests.Conventions;

using System.Reflection;
using BusinessApp.AccountingCore.Server.Journals.Application;
using BusinessApp.AccountingCore.Server.Masters.Application;
using BusinessApp.AccountingCore.Server.Settings.Application;
using BusinessApp.Partners.Server;
using BusinessApp.ServerSupport;

/// <summary>
/// 利用者に見せてよい差し戻しの例外は、すべて <see cref="RejectedException"/> の派生である。
/// </summary>
/// <remarks>
/// <b>型が、画面に出してよい文言の線である</b>（ADR-0051）。保存の入口はこの型だけを利用者の語として CLB へ返し、
/// それ以外は定型文に差し替える。関門を 1 つ足して <c>Exception</c> から直接派生させると、
/// その差し戻しは<b>利用者の語で書いたのに定型文に化ける</b>——名前で見つけて、基底に乗っていることを確かめる。
/// </remarks>
public class RejectedExceptionConventionTests
{
    private static readonly Assembly[] ServerAssemblies =
    [
        typeof(JournalPostingRejectedException).Assembly,
        typeof(PartnerRejectedException).Assembly,
    ];

    [Fact]
    public void 差し戻しの例外は全部が基底に乗っている()
    {
        var named = ServerAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.Name.EndsWith("RejectedException", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(named);
        Assert.All(named, t => Assert.True(typeof(RejectedException).IsAssignableFrom(t), $"{t.Name} が RejectedException の派生でない"));
        Assert.All(named, t => Assert.True(t.IsSealed, $"{t.Name} は sealed にする（文言の作り方を派生で変えさせない）"));
    }

    /// <summary>基底に乗った型は、見出しから始まる利用者の語を持つ（内部表現の英語名を含まない）。</summary>
    [Fact]
    public void 差し戻しの文は見出しから始まる()
    {
        var samples = new RejectedException[]
        {
            new JournalPostingRejectedException([], JournalPostingRejectedException.SavingHeadline),
            new MasterRejectedException("理由。"),
            new CompanyProfileRejectedException("理由。"),
            new PartnerRejectedException("理由。"),
            new PartnerRegistrationRejectedException("理由。"),
        };

        Assert.All(samples, e => Assert.EndsWith("できません。", e.Message.Split('。')[0] + "。", StringComparison.Ordinal));
        Assert.All(samples, e => Assert.DoesNotContain("\n", e.Message, StringComparison.Ordinal));
    }
}
