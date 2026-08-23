namespace BusinessApp.AccountingCore.Periods;

/// <summary>
/// 会計年度と月次期間の集合（docs/04 §7）。
/// </summary>
/// <remarks>
/// 期間の解決は「対象日の月初日で引き当てる」のではなく、<b>日付そのものの範囲比較</b>で行う。
/// 日付を <see cref="DateOnly"/> で扱うため、時刻成分による取りこぼし（qa/01 A-04）が構造的に起きない。
/// </remarks>
public sealed class FiscalCalendar
{
    private readonly IReadOnlyList<AccountingPeriod> _periods;
    private readonly IReadOnlyDictionary<FiscalYearId, FiscalYear> _fiscalYearsById;

    public FiscalCalendar(IEnumerable<FiscalYear> fiscalYears, IEnumerable<AccountingPeriod> periods)
    {
        ArgumentNullException.ThrowIfNull(fiscalYears);
        ArgumentNullException.ThrowIfNull(periods);
        _fiscalYearsById = fiscalYears.ToDictionary(y => y.Id);
        _periods = periods.OrderBy(p => p.Period.From).ToList();

        var overlapping = _periods
            .Zip(_periods.Skip(1), (earlier, later) => (earlier, later))
            .FirstOrDefault(pair => pair.earlier.Period.Overlaps(pair.later.Period));
        if (overlapping.earlier is not null)
        {
            throw new ArgumentException(
                $"会計期間が重複している: {overlapping.earlier.Id.Value}（{overlapping.earlier.Period}）と {overlapping.later.Id.Value}（{overlapping.later.Period}）",
                nameof(periods));
        }
    }

    /// <summary>計上日が属する会計期間を返す。どの期間にも属さなければ null（I-03 の違反）。</summary>
    public AccountingPeriod? ResolvePeriod(DateOnly postingDate)
        => _periods.FirstOrDefault(p => p.Period.Includes(postingDate));

    public FiscalYear? FindFiscalYear(FiscalYearId fiscalYearId)
        => _fiscalYearsById.TryGetValue(fiscalYearId, out var year) ? year : null;

    /// <summary>その日に仕訳を計上できるか。期間が無い・期間が締め済み・年度が締め済みのいずれでも計上できない。</summary>
    public bool IsPostable(DateOnly postingDate)
    {
        var period = ResolvePeriod(postingDate);
        if (period is null || period.Status == PeriodStatus.Closed)
        {
            return false;
        }
        return FindFiscalYear(period.FiscalYearId) is { Status: PeriodStatus.Open };
    }
}
