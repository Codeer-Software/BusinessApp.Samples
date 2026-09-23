namespace BusinessApp.AccountingCore.Server.ConsumptionTax.Infrastructure;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.ServerSupport;
using Codeer.LowCode.Blazor.DataIO.Db;

/// <summary>
/// 制度ルール（税率）を DB から読む（ADR-0069・docs/11 §1-1）。
/// </summary>
/// <remarks>
/// <para><b>値はコードに書かない</b>（docs/20 §2）。ここは<b>行を読んでドメインの型へ移すだけ</b>で、
/// 何 % かを知らない。<b>区分ごとの期間の重なりを弾くのは <see cref="TaxRateBook"/></b> である——
/// 重なりは DB のトリガも止めているので、<b>同じことを 2 か所が独立に見ている</b>。</para>
/// <para><b>国税は万分率の整数、地方は分数のまま移す。</b> ここで小数に直さない——
/// 78 分の 22 は小数で終わらず、読んだ時点で丸めが入る（<see cref="TaxRate"/>）。</para>
/// <para><b>終期は NULL を許す。</b> 消税法 29 も地方税法 72 の 83 も期限を書いていない
/// （<c>docs/research/2026-09-23_消費税の税率と地方消費税の税率.md</c> §1）ので、
/// <see cref="EffectivePeriod"/> の「終期なし」がこの表からは普通に生まれる
/// （<see cref="TransitionRuleLoader"/> はその逆である）。</para>
/// </remarks>
public sealed class TaxRateLoader(IDbAccessor dbAccessor, string dataSourceName)
{
    /// <summary>税率の行を全件読む。</summary>
    /// <remarks>
    /// <b>全件読む。</b> 条文が定めた数本しかなく、<b>明細ごとに引き直すより 1 回読む方が速い</b>
    /// （<see cref="TransitionRuleLoader"/> と同じ判断）。
    /// </remarks>
    public async Task<TaxRateBook> LoadAsync()
    {
        var rows = await dbAccessor.QueryAsync(
            dataSourceName,
            "select rate_kind, valid_from, valid_to, national_rate_per_10000,"
            + " local_numerator, local_denominator, version from tax_rates",
            []);

        return new TaxRateBook(rows.Select(row => new TaxRate(
            DbValue.ToEnum<TaxRateKind>(row["rate_kind"]),
            new EffectivePeriod(DbValue.ToDate(row["valid_from"]), DbValue.ToNullableDate(row["valid_to"])),
            DbValue.ToInt(row["national_rate_per_10000"]),
            DbValue.ToInt(row["local_numerator"]),
            DbValue.ToInt(row["local_denominator"]),
            new RuleVersion(DbValue.ToText(row["version"])))));
    }
}
