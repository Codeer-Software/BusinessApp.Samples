namespace BusinessApp.AccountingCore.Tests.ConsumptionTax;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 税率の 1 行が持つ意味（docs/11 §1-1）。<b>配っている行そのものを読んで検算する。</b>
/// </summary>
/// <remarks>
/// <para>条文と逐語は docs/research/2026-09-23_消費税の税率と地方消費税の税率.md、
/// 配る行は <c>Designer/seed/006_tax_rates.sql</c> が持つ。<b>値をここに書き写さない</b>——
/// 写しを持つと、<b>表を直した回に写しだけが古いまま両方緑になる</b>
/// （2026-09-23 に経過措置の控除割合で実際に起きた。<see cref="StatutoryData"/>）。</para>
/// <para><b>ここで固定するのは「1 行から導ける関係」である。</b> 何 % かは表が決める。
/// <b>引き当て（区分と日付で 1 本選ぶ）は <see cref="TaxRateBookTests"/></b> が見る。</para>
/// </remarks>
public class TaxRateTests
{
    private static TaxRate Delivered(TaxRateKind kind)
        => StatutoryData.TaxRates.Single(rate => rate.Kind == kind);

    /// <summary>
    /// <b>国税と地方から合計税率が出る。</b>
    /// </summary>
    /// <remarks>
    /// <b>合計税率を表に持たない根拠がここである</b>（docs/11 §1-1・<c>Designer/ddl/015</c>）。
    /// 地方消費税は消費税額を課税標準とするので、合計は
    /// <c>国税 ×（分母 ＋ 分子）÷ 分母</c> になる——780 × 100 ÷ 78 ＝ 1000（10%）。
    /// </remarks>
    [Theory]
    [InlineData(TaxRateKind.Standard, 780, 1000)]
    [InlineData(TaxRateKind.Reduced, 624, 800)]
    public void 合計税率は国税と地方から導ける(TaxRateKind kind, int national, int combined)
    {
        var rate = Delivered(kind);

        Assert.Equal(national, rate.NationalRatePer10000);
        Assert.Equal(combined, rate.CombinedRatePer10000);
    }

    /// <summary>
    /// <b>分母が 78 でない分数でも、合計税率を導ける</b>（630 × 80 ÷ 63 ＝ 800）。
    /// </summary>
    /// <remarks>
    /// <b>配っている行はどちらも 78 分の 22 なので、配った行だけでは分母を取り違えた式を赤くできない</b>
    /// （<c>国税 × 100 ÷ 78</c> と書いても 2 行とも合う）。改正前の税率（旧税率。国 6.3%・地方は 63 分の 17）の形を、区分を標準にして直に組む——
    /// <b>その行は配っていない</b>（旧税率はスコープの外——docs/11 §1-1-1。いつから 6.3% かも確かめていない——税率リサーチ §3）。<b>期間の日付はテストの字で、制度の日付ではない</b>。公式を確かめるためだけに置く。
    /// </remarks>
    [Fact]
    public void 分母が78でない分数でも合計税率を導ける()
    {
        var rate = new TaxRate(
            TaxRateKind.Standard, new EffectivePeriod(new DateOnly(2014, 4, 1), new DateOnly(2019, 9, 30)), 630, 17, 63, new RuleVersion("検体"));

        Assert.Equal(800, rate.CombinedRatePer10000);
    }

    /// <summary>
    /// <b>税込を含む換算率が、1 行から整数の分数のまま組み立てられる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>消費税リサーチ §5-1・§5-2・§5-4 が並べている換算率そのものである。</b>
    /// 分母をそろえて <c>10000 ＋ 合計税率</c> にすると、
    /// <b>税込 11,000 に対して 課税標準 10,000・税額 1,000・国税 780</b> と並ぶ——
    /// これは §5-4 の <c>100/110</c>・<c>10/110</c>・<c>7.8/110</c> と同じ分数である
    /// （軽減は <c>100/108</c>・<c>8/108</c>・<c>6.24/108</c>）。</para>
    /// <para><b>整数の対で組み立てられることが要る。</b> 小数に直すと <c>100m/108m</c> は真の値を下回り、
    /// 税込 108,000 円の割戻しが 99,999.99… になって<b>千円未満切捨てで 1,000 円ずれる</b>——
    /// <b>だから合計税率を万分率の整数で導いている</b>（docs/11 §1-1）。</para>
    /// <para><b>積上げの 78/100 はここに無い。この表から導いてはいけない。</b>
    /// 78/100 は、消費税リサーチ §5-2 が消税令 46 ①② の定める固定値として書いている（条文は未読——税率リサーチ §3）。
    /// <b>率から導くと、制度の値を 2 か所（条文の固定値と、率からの計算）が持つことになる。</b></para>
    /// </remarks>
    [Theory]
    [InlineData(TaxRateKind.Standard, 11000, 1000, 780)]
    [InlineData(TaxRateKind.Reduced, 10800, 800, 624)]
    public void 税込の換算率は1行から整数の分数で組み立てられる(
        TaxRateKind kind, int taxIncluded, int tax, int national)
    {
        var rate = Delivered(kind);

        Assert.Equal(
            (taxIncluded, 10000, tax, national),
            (10000 + rate.CombinedRatePer10000,
             10000,
             rate.CombinedRatePer10000,
             rate.NationalRatePer10000));
    }

    /// <summary>
    /// <b>国税の税率が万分率の範囲を外れた行は作れない。</b>
    /// </summary>
    /// <remarks>
    /// <b>DDL と同じ不変条件を、読み出しの側でも確かめる</b>（ADR-0069 の「2 か所が独立に見る」）。
    /// 取込・直打ちで <c>Designer/ddl/015</c> の CHECK を迂回した行が入りうる。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void 国税の税率が範囲外の行は作れない(int national)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RateOf(national, 22, 78));
    }

    /// <summary>
    /// <b>地方消費税が真分数でない行は作れない。</b>
    /// </summary>
    /// <remarks>
    /// <b>分子と分母を取り違えた行（78/22）は、DDL の型も正も版の字も日付も全部通る</b>
    /// ——止めているのは <c>local_numerator &lt; local_denominator</c> の CHECK と、ここである。
    /// <b>分母 0 は <see cref="TaxRate.CombinedRatePer10000"/> で 0 除算になる。</b>
    /// </remarks>
    [Theory]
    [InlineData(78, 22, "分子と分母が逆")]
    [InlineData(78, 78, "分子と分母が同じ")]
    [InlineData(22, 0, "分母が 0")]
    [InlineData(0, 78, "分子が 0")]
    [InlineData(-22, 78, "分子が負")]
    public void 地方消費税が真分数でない行は作れない(int numerator, int denominator, string label)
    {
        var exception = Assert.Throws<ArgumentException>(() => RateOf(780, numerator, denominator));

        Assert.Contains("0 より大きく 1 より小さい分数", exception.Message, StringComparison.Ordinal);
        Assert.NotEmpty(label);
    }

    /// <summary>
    /// <b>合計税率が万分率の整数にならない行は作れない。</b>
    /// </summary>
    /// <remarks>
    /// <c>(800, 22, 78)</c> は他の不変条件を全部満たすが、800 × 100 ÷ 78 ＝ 1025.64… で割り切れない。
    /// <b>入れてしまうと <see cref="TaxRate.CombinedRatePer10000"/> が黙って切り捨てた値を返し、
    /// 税込の換算が 1 行からは作れなくなる</b>。
    /// </remarks>
    [Fact]
    public void 合計税率が万分率の整数にならない行は作れない()
    {
        var exception = Assert.Throws<ArgumentException>(() => RateOf(800, 22, 78));

        Assert.Contains("万分率の整数にならない", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>上限の 10000 は通る。</b> 締めすぎた守りは、拒む側のテストでは 1 本も失敗しない。
    /// </summary>
    /// <remarks>
    /// <b>下限の 1 は、この型では観測できない。</b> 合計税率が万分率の整数になるという不変条件が
    /// <c>国税 ×（分母＋分子）÷ 分母</c> が整数になることを求めるので、
    /// <b>国税を 1 にすると、それを満たす真分数（分子 &lt; 分母）が無い</b>
    /// ——分子が分母の倍数でなければならず、真分数ではそうならない。
    /// <b>だから下限を 2 に締めても、どのテストも失敗しない</b>——観測できないことをここに書き残す。
    /// </remarks>
    [Fact]
    public void 国税の税率は上限値まで通る()
    {
        // 1/2 なら 10000 × 3 ÷ 2 ＝ 15000 で割り切れる。**帯の端そのものを撃つための組である。**
        Assert.Equal(10000, RateOf(10000, 1, 2).NationalRatePer10000);
    }

    private static TaxRate RateOf(int national, int numerator, int denominator)
        => new(
            TaxRateKind.Standard,
            new EffectivePeriod(new DateOnly(2019, 10, 1), null),
            national,
            numerator,
            denominator,
            new RuleVersion("tax_rate:standard:2019-10-01"));
}
