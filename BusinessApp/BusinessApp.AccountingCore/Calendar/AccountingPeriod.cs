namespace BusinessApp.AccountingCore.Calendar;

using BusinessApp.AccountingCore.Primitives;

/// <summary>月次の会計期間（docs/04 §7）。</summary>
/// <param name="Id">期間 ID。</param>
/// <param name="FiscalYearId">属する会計年度の ID。</param>
/// <param name="Period">期間（月初日〜月末日。両端を含む）。</param>
/// <param name="Status">締めの状態。</param>
public sealed record AccountingPeriod(string Id, string FiscalYearId, EffectivePeriod Period, PeriodStatus Status);
