namespace BusinessApp.AccountingCore.Periods;

/// <summary>
/// 会計年度と月次期間の集合（docs/15 §2）。
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

    /// <summary>
    /// <paramref name="date"/> が、<paramref name="fiscalYearId"/> の会計年度より<b>前</b>にあるか。
    /// </summary>
    /// <remarks>
    /// <para><b>前後は年度の開始日で決める。識別子の大小では決めない。</b>
    /// 採番の順と期間の順は別のもので、<b>後から過去の年度を足せば逆転する</b>
    /// （開発機の第 17 期がその形——<c>Designer/seed/dev/002_prior_fiscal_year.sql</c>。
    /// 第 18 期より後に入れたので、識別子は大きいのに期間は前である）。</para>
    /// <para><b>開始日が同じ年度は「前」ではない。</b> 年度の重なりは
    /// <see cref="FiscalCalendar"/> も DDL も禁じていない（docs/04 §5 の未決事項）ので、
    /// <b>重なった年度に対してこの判定は意味を持たない</b>——重なりを止めるのが先である。</para>
    /// <para><b>年度を引けなければ false。</b> 分からないときに「前だ」と答えると、
    /// 呼び手（取消・訂正の確認文）が当期の伝票に嘘の断りを付ける。</para>
    /// <para><b>この判定が「課税期間より前か」の代わりになるのは、課税期間＝会計年度のときだけ</b>である
    /// （docs/02 §3。対象ペルソナは課税期間の特例（消税法 19）を使わない）。
    /// <b>特例を選んだ事業者では、同じ年度の中の前の課税期間を見分けられない。</b></para>
    /// </remarks>
    public bool IsBeforeFiscalYear(DateOnly date, FiscalYearId fiscalYearId)
        => FindFiscalYear(fiscalYearId) is FiscalYear year && date < year.Period.From;

    /// <summary>
    /// <paramref name="date"/> が属する会計年度。ただし <paramref name="current"/> より前のときだけ返す。
    /// </summary>
    /// <remarks>
    /// <para><b>「過年度のものだ」と言うだけでなく、その年度の表示名を確認文に入れられるようにする</b>
    /// ための判定である（取消・訂正の確認文が使う。docs/11 §5-2）。
    /// <b>「前期以前」のような範囲の言い方では、年度が 3 つ以上あるときに
    /// どの年度の話か読み手が決められない。</b></para>
    /// <para><b>その日の会計期間が無ければ null。</b> 表示名を出せないので、
    /// <see cref="IsBeforeFiscalYear"/> が真でも返さない——
    /// <b>アプリに無い年度を「その年度」と呼ぶと、利用者は一覧で探して見つけられない。</b></para>
    /// </remarks>
    public FiscalYear? FindEarlierFiscalYear(DateOnly date, FiscalYearId current)
    {
        if (ResolvePeriod(date) is not AccountingPeriod period)
        {
            return null;
        }

        if (FindFiscalYear(period.FiscalYearId) is not FiscalYear year)
        {
            return null;
        }

        return IsBeforeFiscalYear(date, current) ? year : null;
    }

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
