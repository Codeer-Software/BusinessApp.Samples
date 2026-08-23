namespace BusinessApp.AccountingCore.Periods;

/// <summary>
/// 会計年度の識別子（<c>fiscal_years.id</c>）。
/// </summary>
/// <remarks>
/// 生の <see cref="long"/> を使わないのは、会計コアでは同じ形の識別子が大量に流れるためである
/// （ADR-0014）。素の数値だと取り違えてもコンパイルが通り、<b>数字は出るが中身が違う</b>という
/// 最悪の壊れ方をする。
/// </remarks>
/// <param name="Value">DB の主キー。</param>
public readonly record struct FiscalYearId(long Value);
