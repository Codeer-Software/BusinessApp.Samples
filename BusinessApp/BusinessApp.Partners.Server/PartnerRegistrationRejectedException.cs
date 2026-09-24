namespace BusinessApp.Partners.Server;

using BusinessApp.ServerSupport;

/// <summary>
/// 登録の保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para><b>見出しから始める</b>（2026-08-31 の自己レビュー R26-26）。理由は
/// <see cref="PartnerRejectedException"/> と同じ。</para>
/// <para><b>見出しは押したボタンで決まる</b>（qa/02 R24-23）。登録番号の編集画面のボタンは
/// 「登録」なので、新規でも「終わり」の記録でも「登録できません」と断る。
/// <b>削除だけは「削除できません」</b>——画面に消す手は無い（<c>CanDelete: false</c>）が、API と取込からは来る。
/// 「登録できません。登録の行は削除できません。」と断っていた（2026-09-24 に見出しの網を広げて見つけた）。</para>
/// <para><b>理由は束ねて運ぶ</b>（docs/21 §2-6 の (b)）——関門は違反を全部集めてから 1 回で投げる。
/// 形（「見出し（N 件）。①…②…」・改行を入れない）は <see cref="RejectionMessage"/> が持つ。</para>
/// </remarks>
public sealed class PartnerRegistrationRejectedException(IReadOnlyList<string> reasons, string headline)
    : RejectedException(RejectionMessage.Compose(headline, reasons))
{
    /// <summary>登録番号の登録を止めたときの見出し。</summary>
    public const string Headline = "登録できません";

    /// <summary>登録番号の行の削除を止めたときの見出し。</summary>
    public const string DeletionHeadline = "削除できません";

    /// <summary>登録を止める（見出しは <see cref="Headline"/>）。</summary>
    public PartnerRegistrationRejectedException(IReadOnlyList<string> reasons)
        : this(reasons, Headline)
    {
    }
}
