namespace BusinessApp.AccountingCore.Server.Settings;

/// <summary>
/// 自社情報の保存が関門で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。qa/01 D-12）。
/// </remarks>
public sealed class CompanyProfileRejectedException(string message) : Exception(message);
