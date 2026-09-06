namespace BusinessApp.AccountingCore.ConsumptionTax;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 免税事業者等からの課税仕入れに係る経過措置の控除割合（docs/11 §5・research/2026-08-23_消費税インボイス制度 §1）。
/// </summary>
/// <remarks>
/// <b>割合の値と適用期間はここに書かない。</b> 制度ルールのマスタが持ち、実行時に注入する。
/// 判定は課税期間ではなく<b>課税仕入れを行った日</b>（<c>tax_point</c>）で行う。
/// </remarks>
/// <param name="Period">適用期間（課税仕入れを行った日で判定する）。</param>
/// <param name="Ratio">控除割合（0.7 なら 70%）。</param>
/// <param name="Version">制度ルールの版。</param>
public sealed record TransitionalDeductionRate(EffectivePeriod Period, decimal Ratio, RuleVersion Version)
    : IEffectiveDatedRule
{
    /// <summary>控除割合を仕入税額相当額に適用する。端数処理の方法は用途に応じて呼び出し側が決める（docs/11 §4）。</summary>
    public Yen Apply(Yen taxAmount, RoundingMode mode) => taxAmount.Multiply(Ratio, mode);
}
