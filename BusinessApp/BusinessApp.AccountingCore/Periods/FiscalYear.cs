namespace BusinessApp.AccountingCore.Periods;

using BusinessApp.AccountingCore.Shared;

/// <summary>会計年度（docs/10 §7）。</summary>
/// <param name="Id">会計年度の識別子。</param>
/// <param name="Code">年度コード（例 "FY18"）。</param>
/// <param name="Label">表示名（例「第 18 期（2026 年度）」）。</param>
/// <param name="Period">年度の期間。</param>
/// <param name="Status">締めの状態。</param>
/// <param name="PremiumLedgerFrom">
/// 優良な電子帳簿の適用開始日。課税期間の初日から全帳簿で要件を満たす必要があるため
/// （電帳法 8 ④・電帳令 2）、年度の開始日と一致しない場合は警告する。
/// </param>
public sealed record FiscalYear(
    FiscalYearId Id,
    string Code,
    string Label,
    DateRange Period,
    PeriodStatus Status,
    DateOnly? PremiumLedgerFrom = null);
