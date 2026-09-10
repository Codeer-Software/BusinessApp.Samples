namespace BusinessApp.AccountingCore.Server.Settings.Application;

/// <summary>
/// 自社情報の保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para><b>見出しから始める</b>（2026-08-31 の自己レビュー R26-26）。仕訳の差し戻しは
/// 「計上できません。…」と<b>何ができなかったか</b>を先に言うのに、ここだけ理由から始まっていた。
/// トーストは 1 種類の見た目で見せると決めており（ADR-0023）、文言の形も揃える。</para>
/// <para><b>見出しは押したボタンで決まる</b>（qa/02 R24-23）。自社情報の画面のボタンは「保存」である。</para>
/// <para><b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。qa/01 D-12）。</para>
/// </remarks>
public sealed class CompanyProfileRejectedException(string reason)
    : Exception($"{Headline}。{reason}")
{
    /// <summary>自社情報の保存を止めたときの見出し。</summary>
    public const string Headline = "保存できません";
}
