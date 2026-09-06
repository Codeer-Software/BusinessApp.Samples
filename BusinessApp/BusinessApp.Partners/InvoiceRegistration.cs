namespace BusinessApp.Partners;

/// <summary>
/// 適格請求書発行事業者の登録 1 件（<c>partner_invoice_registrations</c> の 1 行）。
/// </summary>
/// <remarks>
/// 登録は<b>取消・失効・再登録</b>があるので、取引先に 1 列で持てない（docs/13 §3-1）。
/// ここに持つのは「いつからいつまで、どの番号だったか」だけで、出所・確認日・公表名は
/// 取込と監査のための列であり、写しの判定には関わらない。
/// </remarks>
/// <param name="RegistrationNo">登録番号。</param>
/// <param name="ValidFrom">登録年月日（公表システムの registrationDate）。</param>
/// <param name="EndedOn">取消・失効の年月日。登録が生きていれば <c>null</c>。</param>
public readonly record struct InvoiceRegistration(string RegistrationNo, DateOnly ValidFrom, DateOnly? EndedOn);
