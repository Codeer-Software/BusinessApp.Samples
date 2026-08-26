namespace BusinessApp.AccountingCore.Server.Partners;

/// <summary>
/// 取引先の保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。qa/01 D-12）。
/// </remarks>
public sealed class PartnerRejectedException(string message) : Exception(message);
