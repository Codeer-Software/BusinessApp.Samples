namespace BusinessApp.Partners.Server;

/// <summary>
/// 登録の保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para><b>見出しから始める</b>（2026-08-31 の自己レビュー R26-26）。理由は
/// <see cref="PartnerRejectedException"/> と同じ。</para>
/// <para><b>見出しは押したボタンで決まる</b>（qa/02 R24-23）。登録番号の編集画面のボタンは
/// 「登録」なので、新規でも「終わり」の記録でも「登録できません」と断る。</para>
/// <para><b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。qa/01 D-12）。</para>
/// </remarks>
public sealed class PartnerRegistrationRejectedException(string reason)
    : Exception($"{Headline}。{reason}")
{
    /// <summary>登録番号の登録を止めたときの見出し。</summary>
    public const string Headline = "登録できません";
}
