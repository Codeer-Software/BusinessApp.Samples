namespace BusinessApp.AccountingCore.Tests.Periods;

using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 会計期間の解決（docs/15 §2）。
/// 前回プロジェクトは「対象日の月初日で引き当てる」方式で時刻成分の問題を回避していたが、
/// 日付を <see cref="DateOnly"/> で持てば範囲比較で素直に解ける（qa/01 A-04）。
/// </summary>
public class FiscalCalendarTests
{
    [Theory]
    [InlineData("2026-04-01", 1)]
    [InlineData("2026-04-30", 1)]
    [InlineData("2026-05-01", 2)]
    [InlineData("2027-03-31", 12)]
    public void 月末日と月初日を取りこぼさない(string date, long expectedPeriodId)
    {
        var period = AccountingFixture.Calendar().ResolvePeriod(DateOnly.Parse(date));

        Assert.NotNull(period);
        Assert.Equal(new AccountingPeriodId(expectedPeriodId), period.Id);
    }

    [Theory]
    [InlineData("2026-03-31")]
    [InlineData("2027-04-01")]
    public void 年度の外は解決できない(string date)
    {
        Assert.Null(AccountingFixture.Calendar().ResolvePeriod(DateOnly.Parse(date)));
    }

    [Fact]
    public void 会計年度を引ける()
    {
        var calendar = AccountingFixture.Calendar();

        Assert.Equal("第 18 期（2026 年度）", calendar.FindFiscalYear(AccountingFixture.FiscalYear)?.Label);
        Assert.Null(calendar.FindFiscalYear(new FiscalYearId(99)));
        Assert.Null(calendar.FindFiscalYear(default));
    }

    [Fact]
    public void 未締めの期間には計上できる()
    {
        Assert.True(AccountingFixture.Calendar().IsPostable(new DateOnly(2026, 5, 20)));
    }

    [Fact]
    public void 会計期間のない日付には計上できない()
    {
        Assert.False(AccountingFixture.Calendar().IsPostable(new DateOnly(2027, 4, 1)));
    }

    [Fact]
    public void 締め済みの期間には計上できない()
    {
        var calendar = AccountingFixture.Calendar(septemberStatus: PeriodStatus.Closed);

        Assert.False(calendar.IsPostable(new DateOnly(2026, 9, 15)));
        Assert.True(calendar.IsPostable(new DateOnly(2026, 10, 15)));
    }

    [Fact]
    public void 締め済みの年度には計上できない()
    {
        var calendar = AccountingFixture.Calendar(fiscalYearStatus: PeriodStatus.Closed);

        Assert.False(calendar.IsPostable(new DateOnly(2026, 5, 20)));
    }

    [Fact]
    public void 属する年度が存在しない期間には計上できない()
    {
        var orphan = new AccountingPeriod(
            new AccountingPeriodId(99),
            new FiscalYearId(99),
            new DateRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)),
            PeriodStatus.Open);
        var calendar = new FiscalCalendar([], [orphan]);

        Assert.False(calendar.IsPostable(new DateOnly(2026, 4, 15)));
    }

    [Fact]
    public void 重なる会計期間は受け付けない()
    {
        var april = new AccountingPeriod(
            new AccountingPeriodId(1),
            AccountingFixture.FiscalYear,
            new DateRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)),
            PeriodStatus.Open);
        var overlapping = april with { Id = new AccountingPeriodId(2), Period = new DateRange(new DateOnly(2026, 4, 30), new DateOnly(2026, 5, 31)) };

        Assert.Throws<ArgumentException>(() => new FiscalCalendar([], [april, overlapping]));
    }

    [Fact]
    public void 空の暦は作れる()
    {
        var calendar = new FiscalCalendar([], []);

        Assert.Null(calendar.ResolvePeriod(new DateOnly(2026, 5, 20)));
        Assert.False(calendar.IsPostable(new DateOnly(2026, 5, 20)));
    }

    // --- 年度より前か（取消・訂正の確認文が使う。docs/11 §5-2） ---

    /// <summary>
    /// <b>前後は年度の開始日で決まる。識別子の大小では決まらない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>この検体の肝は、識別子の順と期間の順を逆にしてあること</b>である——
    /// 第 17 期の識別子を <b>第 18 期より大きく</b>してある。
    /// <b>開発機の実際の姿がこれ</b>で（<c>Designer/seed/dev/002_prior_fiscal_year.sql</c> は
    /// 第 18 期の後に第 17 期を入れる）、<b>識別子で比べる実装はここで落ちる</b>。</para>
    /// <para>揃えてあると <c>target.Value &lt; current.Value</c> と区別が付かない（qa/03 L-19 の型）。</para>
    /// </remarks>
    [Theory]
    // 第 17 期（2025-04-01 〜 2026-03-31。id は 9）より前・中・後
    [InlineData("2025-03-31", 9, true)]
    [InlineData("2025-04-01", 9, false)]
    [InlineData("2026-03-31", 9, false)]
    // 第 18 期（2026-04-01 〜 2027-03-31。id は 1）から見ると、第 17 期の日はすべて「前」
    [InlineData("2026-03-31", 1, true)]
    [InlineData("2025-04-01", 1, true)]
    [InlineData("2026-04-01", 1, false)]
    [InlineData("2027-03-31", 1, false)]
    [InlineData("2027-04-01", 1, false)]
    public void 年度より前かは開始日で決まる(string date, long fiscalYearId, bool expected)
    {
        Assert.Equal(
            expected,
            TwoYears().IsBeforeFiscalYear(DateOnly.Parse(date), new FiscalYearId(fiscalYearId)));
    }

    /// <summary>
    /// <b>引けない年度は「前ではない」。</b> 分からないときに「前だ」と答えると、
    /// 呼び手（取消・訂正の確認文）が当期の伝票に嘘の断りを付ける。
    /// </summary>
    [Fact]
    public void 引けない年度より前とは言わない()
    {
        // **どれだけ昔の日付でも false** ——「前かどうか分からない」を「前だ」に倒さない。
        Assert.False(TwoYears().IsBeforeFiscalYear(new DateOnly(1900, 1, 1), new FiscalYearId(99)));
        Assert.False(new FiscalCalendar([], []).IsBeforeFiscalYear(new DateOnly(1900, 1, 1), new FiscalYearId(1)));
    }

    /// <summary>
    /// <b>開始日が同じ年度は「前」ではない。</b>
    /// </summary>
    /// <remarks>
    /// 年度の重なりは <see cref="FiscalCalendar"/> も DDL も禁じていない（docs/04 §5 の未決事項）。
    /// <b>ここで表明するのは「重なりを許す」ことではなく、
    /// 重なったときにこの問いがどう答えるかを決めておく</b>ことである。
    /// </remarks>
    [Fact]
    public void 開始日が同じ年度は前ではない()
    {
        var calendar = new FiscalCalendar(
            [Year(1, "FY18", "2026-04-01", "2027-03-31"), Year(2, "FY18B", "2026-04-01", "2026-09-30")],
            []);

        Assert.False(calendar.IsBeforeFiscalYear(new DateOnly(2026, 4, 1), new FiscalYearId(1)));
        Assert.False(calendar.IsBeforeFiscalYear(new DateOnly(2026, 4, 1), new FiscalYearId(2)));

        // **前後が決まるのは開始日が違うときだけ**である。
        Assert.True(calendar.IsBeforeFiscalYear(new DateOnly(2026, 3, 31), new FiscalYearId(1)));
    }

    /// <summary>
    /// <b>終了日では決めない。</b> 開始日と終了日を取り違えた実装をここで落とす。
    /// </summary>
    /// <remarks>
    /// 重ならない年度では開始日の順と終了日の順が一致するので、
    /// <b>ふつうの検体では <c>From</c> を <c>To</c> に書き換えても緑になる</b>。
    /// <b>重なる年度を 1 組だけ置いて、2 つの順が食い違う日を撃つ。</b>
    /// </remarks>
    [Fact]
    public void 終了日では決めない()
    {
        // 長い年度（2026-04-01 〜 2028-03-31）と、その中に始まる短い年度（2026-10-01 〜 2027-03-31）。
        // 開始日の順は 長 → 短、終了日の順は 短 → 長 で**逆になる**。
        var calendar = new FiscalCalendar(
            [Year(1, "LONG", "2026-04-01", "2028-03-31"), Year(2, "SHORT", "2026-10-01", "2027-03-31")],
            []);

        // 2026-05-01 は、短い年度の開始日より前・長い年度の終了日より前。
        // **開始日で見れば true、終了日で見れば false** になる日である。
        Assert.True(calendar.IsBeforeFiscalYear(new DateOnly(2026, 5, 1), new FiscalYearId(2)));
    }

    /// <summary>
    /// <b>前の年度なら、その年度を名乗れる。</b>
    /// </summary>
    /// <remarks>
    /// 確認文が「その年度の消費税の申告」と言うために、<b>ラベルまで返す</b>。
    /// </remarks>
    [Fact]
    public void 前の年度なら年度を返す()
    {
        var calendar = TwoYearsWithPeriods();

        // 取引日が第 17 期にあり、今日が第 18 期なら、第 17 期を名乗る。
        Assert.Equal(
            "FY17 のラベル",
            calendar.FindEarlierFiscalYear(new DateOnly(2026, 3, 20), new FiscalYearId(1))?.Label);

        // 同じ年度なら返さない。
        Assert.Null(calendar.FindEarlierFiscalYear(new DateOnly(2026, 5, 20), new FiscalYearId(1)));

        // 後の年度なら返さない（第 17 期から見た第 18 期の日）。
        Assert.Null(calendar.FindEarlierFiscalYear(new DateOnly(2026, 5, 20), new FiscalYearId(9)));
    }

    /// <summary>
    /// <b>会計期間の無い日は、前でも名乗らない。</b>
    /// </summary>
    /// <remarks>
    /// <b>「前かどうか」と「名乗れるかどうか」は別の問いである。</b>
    /// 2024 年の日は第 18 期より確かに前だが、<b>属する年度が無いので「その年度」と言えない</b>。
    /// </remarks>
    [Fact]
    public void 会計期間の無い日は年度を名乗らない()
    {
        var calendar = TwoYearsWithPeriods();

        // 前であることは真。
        Assert.True(calendar.IsBeforeFiscalYear(new DateOnly(2024, 5, 20), new FiscalYearId(1)));

        // それでも名乗らない。
        Assert.Null(calendar.FindEarlierFiscalYear(new DateOnly(2024, 5, 20), new FiscalYearId(1)));
    }

    /// <summary>
    /// <b>期間はあるのに、その期間の年度が引けないときも名乗らない。</b>
    /// </summary>
    /// <remarks>
    /// <b>DB では外部キーが防ぐ形だが、<see cref="FiscalCalendar"/> は年度と期間を別々に受け取る</b>ので、
    /// 組み立て側が食い違えばこの形になる（<c>PeriodOrphaned</c> の断りが実在するのと同じ理由）。
    /// </remarks>
    [Fact]
    public void 期間の年度が引けないときは名乗らない()
    {
        // 期間だけがあり、その `fiscal_year_id` に当たる年度が無い暦。
        var calendar = new FiscalCalendar(
            [Year(1, "FY18", "2026-04-01", "2027-03-31")],
            [new AccountingPeriod(
                new AccountingPeriodId(99), new FiscalYearId(9),
                new DateRange(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)), PeriodStatus.Open)]);

        Assert.Null(calendar.FindEarlierFiscalYear(new DateOnly(2026, 3, 20), new FiscalYearId(1)));
    }

    /// <summary>第 18 期（id 1）と第 17 期（id 9）に、それぞれ 3 月と 5 月の期間を置いた暦。</summary>
    private static FiscalCalendar TwoYearsWithPeriods()
        => new(
            [Year(1, "FY18", "2026-04-01", "2027-03-31"), Year(9, "FY17", "2025-04-01", "2026-03-31")],
            [
                new AccountingPeriod(
                    new AccountingPeriodId(1), new FiscalYearId(9),
                    new DateRange(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)), PeriodStatus.Open),
                new AccountingPeriod(
                    new AccountingPeriodId(2), new FiscalYearId(1),
                    new DateRange(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31)), PeriodStatus.Open),
            ]);

    /// <summary>
    /// 第 18 期（id 1）と第 17 期（<b>id 9</b>）の 2 年度。<b>識別子の順と期間の順を逆にしてある。</b>
    /// </summary>
    private static FiscalCalendar TwoYears()
        => new(
            [Year(1, "FY18", "2026-04-01", "2027-03-31"), Year(9, "FY17", "2025-04-01", "2026-03-31")],
            []);

    private static FiscalYear Year(long id, string code, string from, string to)
        => new(
            new FiscalYearId(id),
            code,
            $"{code} のラベル",
            new DateRange(DateOnly.Parse(from), DateOnly.Parse(to)),
            PeriodStatus.Open);

    [Fact]
    public void nullでは作れない()
    {
        Assert.Throws<ArgumentNullException>(() => new FiscalCalendar(null!, []));
        Assert.Throws<ArgumentNullException>(() => new FiscalCalendar([], null!));
    }
}
