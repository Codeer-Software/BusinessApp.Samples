namespace BusinessApp.AccountingCore.Server.Masters.Application;

using BusinessApp.ServerSupport;

/// <summary>
/// マスタの保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para><b>見出しから始める</b>（qa/02 R26-26。仕訳・自社情報・取引先と同じ形）。
/// <b>見出しは押したボタンで決まる</b>（qa/02 R24-23）——マスタ 4 画面のボタンは新規でも既存でも「登録」である。</para>
/// <para><b>理由は束ねて運ぶ</b>（docs/21 §2-6 の (b)）——関門は違反を全部集めてから 1 回で投げる。
/// 形（「見出し（N 件）。①…②…」・改行を入れない）は <see cref="RejectionMessage"/> が持つ。</para>
/// </remarks>
public sealed class MasterRejectedException(IReadOnlyList<string> reasons)
    : RejectedException(RejectionMessage.Compose(Headline, reasons))
{
    /// <summary>マスタの登録を止めたときの見出し。</summary>
    public const string Headline = "登録できません";

    /// <summary>理由が 1 つだけのとき（束ねない断り——識別子の読めない更新。<c>MasterMeaningGate</c>）。</summary>
    public MasterRejectedException(string reason)
        : this([reason])
    {
    }
}
