namespace BusinessApp.AccountingCore.Server.Tests.Conventions;

using System.Reflection;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Server.Journals.Application;
using BusinessApp.AccountingCore.Server.Masters.Application;
using BusinessApp.AccountingCore.Server.Settings.Application;
using BusinessApp.Partners.Server;
using BusinessApp.ServerSupport;

/// <summary>
/// 利用者に見せてよい差し戻しの例外は、すべて <see cref="RejectedException"/> の派生である。
/// </summary>
/// <remarks>
/// <b>型が、画面に出してよい文言の線である</b>（ADR-0051）。保存の入口はこの型だけを利用者の語として CLB へ渡し、
/// それ以外は定型文に差し替える。関門を 1 つ足して <c>Exception</c> から直接派生させると、
/// その差し戻しは<b>利用者の語で書いたのに定型文に化ける</b>——<b>2 つの部品にある例外の派生を全数</b>取って、
/// 基底に乗っていることを確かめる（名前で絞ると、名前を外した派生が素通りする）。
/// </remarks>
public class RejectedExceptionConventionTests
{
    private static readonly Assembly[] ServerAssemblies =
    [
        typeof(JournalPostingRejectedException).Assembly,
        typeof(PartnerRejectedException).Assembly,
    ];

    [Fact]
    public void 部品が定義する例外は全部が差し戻しの基底に乗っている()
    {
        var exceptions = ServerAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsSubclassOf(typeof(Exception)))
            .ToList();

        Assert.Equal(5, exceptions.Count);
        Assert.All(exceptions, t => Assert.True(typeof(RejectedException).IsAssignableFrom(t), $"{t.Name} が RejectedException の派生でない"));
        Assert.All(exceptions, t => Assert.True(t.IsSealed, $"{t.Name} は sealed にする（文言の作り方を派生で変えさせない）"));
    }

    /// <summary>
    /// 基底に乗った型は、見出し（「〜できません。」、束ねたときは「〜できません（N 件）。」）から始まり、改行を持たない（qa/01 D-12）。
    /// </summary>
    /// <remarks>
    /// <b>1 件の形と束ねた形の両方で見る</b>——1 件の形だけを見ると、束ねた形の見出しが崩れても緑のまま（2026-09-24 の自己レビュー）。
    /// </remarks>
    [Fact]
    public void 差し戻しの文は見出しから始まる()
    {
        var single = new RejectedException[]
        {
            new JournalPostingRejectedException([new Violation("I-01", "理由。")], JournalPostingRejectedException.SavingHeadline),
            new MasterRejectedException(["理由。"]),
            new CompanyProfileRejectedException(["理由。"]),
            new PartnerRejectedException(["理由。"]),
            new PartnerRegistrationRejectedException(["理由。"]),
            new PartnerRegistrationRejectedException(["理由。"], PartnerRegistrationRejectedException.DeletionHeadline),
        };
        var bundled = new RejectedException[]
        {
            new JournalPostingRejectedException([new Violation("I-01", "理由ア。"), new Violation("I-02", "理由イ。")]),
            new MasterRejectedException(["理由ア。", "理由イ。"]),
            new CompanyProfileRejectedException(["理由ア。", "理由イ。"]),
            new PartnerRejectedException(["理由ア。", "理由イ。"]),
            new PartnerRegistrationRejectedException(["理由ア。", "理由イ。"]),
        };

        Assert.All(single, e => Assert.Matches(@"^[^（。]+できません。理由。$", e.Message));
        Assert.All(bundled, e => Assert.Matches(@"^[^（。]+できません（2 件）。①理由ア。②理由イ。$", e.Message));
        Assert.All([.. single, .. bundled], e => Assert.DoesNotContain("\n", e.Message, StringComparison.Ordinal));
    }
}
