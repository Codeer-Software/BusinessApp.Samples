namespace BusinessApp.AccountingCore.Server.ConsumptionTax.Infrastructure;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.ServerSupport;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 制度ルール（経過措置の控除割合）を DB から読む（ADR-0069）。
/// </summary>
/// <remarks>
/// <para><b>値はコードに書かない</b>（docs/20 §2）。ここは<b>行を読んでドメインの型へ移すだけ</b>で、
/// 何 % かを知らない。<b>期間の重なりを弾くのは <see cref="EffectiveDatedRuleSet{TRule}"/></b> である——
/// 重なりは DB のトリガも止めているので、<b>同じことを 2 か所が独立に見ている</b>。</para>
/// <para><b>割合は整数の百分率で持ち、ここで小数へ直す</b>（条文が「百分の八十」と整数で書くから。
/// ddl/014）。<c>80m / 100m</c> は <c>decimal</c> の割り算なので、2 進小数の丸めが入らない。</para>
/// <para><b>終期は必ずある。</b> 経過措置は条文が終わりを書いており、
/// <b>終わった後は「行が無い」ことで表す</b>（ddl/014）——だから
/// <see cref="EffectivePeriod"/> の「終期なし」は、この表からは生まれない。</para>
/// </remarks>
public sealed class TransitionRuleLoader(IDbAccessor dbAccessor, string dataSourceName)
{
    /// <summary>免税事業者等からの課税仕入れに係る経過措置の控除割合を、全件読む。</summary>
    /// <remarks>
    /// <b>全件読む。</b> 条文が定めた数本しかなく、<b>明細ごとに引き直すより 1 回読む方が速い</b>
    /// （<see cref="Journals.Infrastructure.AccountingMasterLoader"/> と同じ判断）。
    /// </remarks>
    public async Task<EffectiveDatedRuleSet<TransitionalDeductionRate>> LoadDeductionRatesAsync()
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            "select valid_from, valid_to, rate_percent, version from transition_purchase_rates",
            []);

        return new EffectiveDatedRuleSet<TransitionalDeductionRate>(rows.Select(row =>
            new TransitionalDeductionRate(
                new EffectivePeriod(DbValue.ToDate(row["valid_from"]), DbValue.ToDate(row["valid_to"])),
                DbValue.ToLong(row["rate_percent"]) / 100m,
                new RuleVersion(DbValue.ToText(row["version"])))));
    }
}
