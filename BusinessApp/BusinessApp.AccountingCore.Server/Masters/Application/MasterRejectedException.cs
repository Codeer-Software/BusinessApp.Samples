namespace BusinessApp.AccountingCore.Server.Masters.Application;

using BusinessApp.ServerSupport;

/// <summary>
/// マスタの保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para><b>見出しから始める</b>（qa/02 R26-26。仕訳・自社情報・取引先と同じ形）。
/// <b>見出しは押したボタンで決まる</b>（qa/02 R24-23）——マスタ 4 画面のボタンは新規でも既存でも「登録」である。</para>
/// <para><b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。qa/01 D-12）。</para>
/// </remarks>
public sealed class MasterRejectedException(string reason)
    : RejectedException($"{Headline}。{reason}")
{
    /// <summary>マスタの登録を止めたときの見出し。</summary>
    public const string Headline = "登録できません";
}
