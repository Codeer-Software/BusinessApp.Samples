namespace BusinessApp.Partners.Server;

using BusinessApp.ServerSupport;

/// <summary>
/// 取引先の保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para><b>見出しから始める。</b> 会計側は「計上できません。…」「保存できません。…」と
/// <b>何ができなかったか</b>を先に言うのに、取引先だけ理由から始まっていた
/// （2026-08-31 の自己レビュー R26-26）。トーストは 1 種類の見た目で見せると決めており
/// （ADR-0023）、<b>文言の形も揃っているほうが読み手に一貫する</b>。</para>
/// <para><b>見出しは押したボタンで決まる</b>（qa/02 R24-23）。取引先の詳細のボタンは「登録」なので
/// 「登録できません」と断る。関門がどこで捕まえたかではなく、利用者がした操作の言葉で言う。</para>
/// <para><b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。qa/01 D-12）。</para>
/// </remarks>
public sealed class PartnerRejectedException(string reason)
    : RejectedException($"{Headline}。{reason}")
{
    /// <summary>取引先の登録を止めたときの見出し。</summary>
    public const string Headline = "登録できません";
}
