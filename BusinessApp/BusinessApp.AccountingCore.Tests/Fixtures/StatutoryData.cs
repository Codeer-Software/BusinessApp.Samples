namespace BusinessApp.AccountingCore.Tests.Fixtures;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// テストで使う制度値。<b>本番の制度値は制度ルールのマスタが持つ</b>（docs/20_実装の原則.md §2）。
/// ここに置くのは「マスタにこの値が入っていたとき、コアがどう振る舞うか」を固定するためである。
/// </summary>
/// <remarks>
/// 値の根拠は docs/research/2026-08-23_消費税インボイス制度.md §1-1。
/// 出典: https://www.nta.go.jp/taxes/shiraberu/zeimokubetsu/shohi/keigenzeiritsu/pdf/qa/01-15.pdf 、
/// https://www.mof.go.jp/tax_policy/tax_reform/outline/fy2026/08taikou_04.htm （確認日 2026-08-23）
/// 根拠法: 28 年改正法附則 52・53、令和 8 年法律第 12 号（令和 8 年 3 月 31 日成立・公布）。
/// </remarks>
public static class StatutoryData
{
    /// <summary>
    /// 免税事業者等からの課税仕入れに係る経過措置の控除割合（<b>令和 8 年度改正後</b>＝現行法上の確定値）。
    /// 適用期限を 2029-09-30 → 2031-09-30 に 2 年延長した上で、引下げペースを緩和したもの。
    /// </summary>
    public static IReadOnlyList<TransitionalDeductionRate> TransitionalDeductionRates { get; } =
    [
        Rate("2023-10-01", "2026-09-30", 0.80m),
        Rate("2026-10-01", "2028-09-30", 0.70m),
        Rate("2028-10-01", "2029-09-30", 0.50m),
        Rate("2029-10-01", "2030-09-30", 0.50m),
        Rate("2030-10-01", "2031-09-30", 0.30m),
        Rate("2031-10-01", null, 0.00m),
    ];

    /// <summary>
    /// <b>令和 8 年度改正前</b>の控除割合。改正で過去の仕訳の計算結果が変わらないこと（I-16）の検証に使う。
    /// </summary>
    public static IReadOnlyList<TransitionalDeductionRate> TransitionalDeductionRatesBeforeAmendment { get; } =
    [
        Rate("2023-10-01", "2026-09-30", 0.80m, "pre-amendment"),
        Rate("2026-10-01", "2029-09-30", 0.50m, "pre-amendment"),
        Rate("2029-10-01", null, 0.00m, "pre-amendment"),
    ];

    public static EffectiveDatedRuleSet<TransitionalDeductionRate> TransitionalDeductionRuleSet()
        => new(TransitionalDeductionRates);

    public static EffectiveDatedRuleSet<TransitionalDeductionRate> TransitionalDeductionRuleSetBeforeAmendment()
        => new(TransitionalDeductionRatesBeforeAmendment);

    private static TransitionalDeductionRate Rate(string from, string? to, decimal ratio, string? suffix = null)
    {
        var period = new EffectivePeriod(DateOnly.Parse(from), to is null ? null : DateOnly.Parse(to));
        var version = new RuleVersion($"transitional-deduction@{from}{(suffix is null ? string.Empty : "/" + suffix)}");
        return new TransitionalDeductionRate(period, ratio, version);
    }
}
