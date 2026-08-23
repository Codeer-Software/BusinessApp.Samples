namespace BusinessApp.AccountingCore.Calendar;

using BusinessApp.AccountingCore.Primitives;

/// <summary>締めの状態（docs/04 §7）。</summary>
public enum PeriodStatus
{
    /// <summary>未締め。仕訳を計上できる。</summary>
    Open,

    /// <summary>締め済み。計上・訂正・取消はできない（I-04）。</summary>
    Closed,
}

/// <summary>月次の会計期間（docs/04 §7）。</summary>
/// <param name="Id">期間 ID。</param>
/// <param name="FiscalYearId">属する会計年度の ID。</param>
/// <param name="Period">期間（月初日〜月末日。両端を含む）。</param>
/// <param name="Status">締めの状態。</param>
public sealed record AccountingPeriod(string Id, string FiscalYearId, EffectivePeriod Period, PeriodStatus Status);

/// <summary>会計年度（docs/04 §7）。</summary>
/// <param name="Id">会計年度 ID。</param>
/// <param name="Label">表示名（例「第 18 期（2026 年度）」）。</param>
/// <param name="Period">年度の期間。</param>
/// <param name="Status">締めの状態。</param>
/// <param name="PremiumLedgerFrom">優良な電子帳簿の適用開始日。課税期間の初日と一致しない場合は警告する。</param>
public sealed record FiscalYear(
    string Id,
    string Label,
    EffectivePeriod Period,
    PeriodStatus Status,
    DateOnly? PremiumLedgerFrom = null);
