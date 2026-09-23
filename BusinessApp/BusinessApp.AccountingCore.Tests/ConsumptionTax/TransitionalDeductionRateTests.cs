namespace BusinessApp.AccountingCore.Tests.ConsumptionTax;

using System.Globalization;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 免税事業者等からの課税仕入れに係る経過措置（docs/11 §5）。
/// <b>会計コア最初のゴールデンテスト</b>であり、2026-10-01 の 80%→70% の切替を境界で固定する。
/// </summary>
/// <remarks>
/// 出典: https://www.nta.go.jp/taxes/shiraberu/zeimokubetsu/shohi/keigenzeiritsu/pdf/qa/01-15.pdf 、
/// https://www.mof.go.jp/tax_policy/tax_reform/outline/fy2026/08taikou_04.htm （確認日 2026-08-23）
/// 詳細は docs/research/2026-08-23_消費税インボイス制度.md §1。
/// </remarks>
public class TransitionalDeductionRateTests
{
    private static readonly EffectiveDatedRuleSet<TransitionalDeductionRate> Rules =
        StatutoryData.TransitionalDeductionRuleSet();

    [Theory]
    [InlineData("2023-10-01", 0.80)]
    [InlineData("2026-09-29", 0.80)]
    [InlineData("2026-09-30", 0.80)]  // 切替の前日
    [InlineData("2026-10-01", 0.70)]  // 切替日
    [InlineData("2026-10-02", 0.70)]
    [InlineData("2028-09-30", 0.70)]
    [InlineData("2028-10-01", 0.50)]
    [InlineData("2030-09-30", 0.50)]
    [InlineData("2030-10-01", 0.30)]
    [InlineData("2031-09-30", 0.30)]
    public void 控除割合は課税仕入れを行った日で決まる(string taxPoint, double expected)
    {
        var rate = Rules.ResolveAt(DateOnly.Parse(taxPoint));

        Assert.NotNull(rate);
        Assert.Equal((decimal)expected, rate.Ratio);
    }

    /// <summary>
    /// <b>経過措置の外では割合が引けない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>始まる前と終わった後を、同じ形で表す</b>（ddl/014・docs/11 §5-1）。
    /// <b>2026-09-23 まで、終わった後だけ 0% の行を置いていた</b>——
    /// 同じ「経過措置は適用されない」を 2 通りで表しており、
    /// <b>0% の行は「経過措置を 0% で適用する」と読める</b>（帳簿に旨が立ち、上限の累計にも入る）。</para>
    /// <para><b>引けなかったことを「経過措置なし＝全額控除」と読まない。</b>
    /// 未登録の相手からの課税仕入れは、経過措置が無ければ控除できない——
    /// <b>計上の関門が止める</b>（docs/11 §5-3）。</para>
    /// </remarks>
    [Theory]
    [InlineData("2023-09-30")]  // 五年施行日の前日
    [InlineData("2031-10-01")]  // 経過措置の最終日の翌日
    public void 経過措置の外では割合が引けない(string taxPoint)
    {
        Assert.Null(Rules.ResolveAt(DateOnly.Parse(taxPoint, CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// 商品仕入れは引渡日で判定するため、<b>1 契約でも日付をまたげば割合が分かれる</b>
    /// （docs/research §1-2 の設例）。
    /// </summary>
    [Fact]
    public void 同一契約でも引渡日が切替をまたげば割合が分かれる()
    {
        var before = Rules.ResolveAt(new DateOnly(2026, 9, 30));
        var after = Rules.ResolveAt(new DateOnly(2026, 10, 1));

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(0.80m, before.Ratio);
        Assert.Equal(0.70m, after.Ratio);
    }

    /// <summary>役務提供は全部が完了した日で判定する。期間が切替をまたいでも完了日の割合が全額に及ぶ。</summary>
    [Fact]
    public void 役務提供は完了日の割合が全額に及ぶ()
    {
        var rate = Rules.ResolveAt(new DateOnly(2026, 10, 20));

        Assert.NotNull(rate);
        Assert.Equal(0.70m, rate.Ratio);
    }

    [Theory]
    [InlineData(RoundingMode.Truncate, 5443)]
    [InlineData(RoundingMode.Round, 5444)]
    public void 控除額は仕入税額相当額に割合を掛けて求める(RoundingMode mode, long expected)
    {
        var rate = Rules.ResolveAt(new DateOnly(2026, 10, 1));

        Assert.NotNull(rate);
        // 仕入税額相当額 7,777 円 × 70% = 5,443.9 円
        Assert.Equal(Yen.From(expected), rate.Apply(Yen.From(7_777), mode));
    }

    /// <summary>
    /// I-16。令和 8 年度改正で 2026-10-01 以後の割合は 50%→70% に変わったが、
    /// <b>版で引けば改正前の割合が再現できる</b>。過去の仕訳を再計算しないための土台。
    /// </summary>
    [Fact]
    public void 制度が改正されても保存した版で過去の割合を再現できる()
    {
        var beforeAmendment = StatutoryData.TransitionalDeductionRuleSetBeforeAmendment();
        var appliedVersion = beforeAmendment.ResolveAt(new DateOnly(2026, 10, 1))!.Version;

        Assert.Equal(0.50m, beforeAmendment.ResolveByVersion(appliedVersion)!.Ratio);
        Assert.Equal(0.70m, Rules.ResolveAt(new DateOnly(2026, 10, 1))!.Ratio);
    }

    /// <summary>
    /// <b>経過措置の窓が、両端とも閉じていて 1 日も欠けていない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>配った行そのものを見ている</b>（<see cref="StatutoryData"/> は seed を読む）。
    /// <b>1 行足りなければここが赤くなる</b>——稼働 DB から行が消える事故は
    /// <c>migrate.ps1 -Verify</c> が捕まえるが、<b>seed と配達が最初から 1 行足りない</b>場合は
    /// 同値検査では捕まらない（どちらも同じだけ足りないので）。</para>
    /// <para><b>窓の両端を字で書く。</b> 連続だけを見ると、窓ごと別の年へずらしても緑のまま通る。</para>
    /// </remarks>
    [Fact]
    public void 経過措置の窓は両端が閉じていて隙間が無い()
    {
        var rules = Rules.Rules;

        Assert.Equal(new DateOnly(2023, 10, 1), rules[0].Period.From);
        Assert.Equal(new DateOnly(2031, 9, 30), rules[^1].Period.To);
        Assert.All(rules, rule => Assert.NotNull(rule.Period.To));

        foreach (var (earlier, later) in rules.Zip(rules.Skip(1)))
        {
            Assert.Equal(earlier.Period.To!.Value.AddDays(1), later.Period.From);
        }
    }
}
