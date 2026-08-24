namespace BusinessApp.AccountingCore.Periods;

using BusinessApp.AccountingCore.Shared;

/// <summary>月次の会計期間（docs/04 §7）。</summary>
/// <param name="Id">会計期間の識別子。</param>
/// <param name="FiscalYearId">属する会計年度。</param>
/// <param name="Period">期間（月初日〜月末日。両端を含む）。</param>
/// <param name="Status">締めの状態。</param>
public sealed record AccountingPeriod(
    AccountingPeriodId Id,
    FiscalYearId FiscalYearId,
    DateRange Period,
    PeriodStatus Status);
