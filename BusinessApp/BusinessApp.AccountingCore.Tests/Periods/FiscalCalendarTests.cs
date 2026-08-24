namespace BusinessApp.AccountingCore.Tests.Periods;

using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 会計期間の解決（docs/04 §7）。
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

    [Fact]
    public void nullでは作れない()
    {
        Assert.Throws<ArgumentNullException>(() => new FiscalCalendar(null!, []));
        Assert.Throws<ArgumentNullException>(() => new FiscalCalendar([], null!));
    }
}
