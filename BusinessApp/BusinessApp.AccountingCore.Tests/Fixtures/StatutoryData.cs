namespace BusinessApp.AccountingCore.Tests.Fixtures;

using System.Globalization;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.TestSupport;

/// <summary>
/// テストで使う制度値。<b>本番の制度値は制度ルールのマスタが持つ</b>（docs/20_実装の原則.md §2）。
/// </summary>
/// <remarks>
/// <para><b>配っている行そのものを読む</b>（<c>Designer/ddl/014</c> ＋ <c>Designer/seed/005</c>）。
/// <b>値を書き写さない</b>——2026-09-23 まで写しを持っており、
/// <b>表を作ったときに写しだけが古いまま両方緑</b>になっていた（6 行・0% の行つき・50% を 2 分割で、
/// <b>その形は本番の表に物理的に入らない</b>）。ゴールデンテストが守っていたのは、
/// <b>本番に存在しない制度像</b>だった。</para>
/// <para>値の根拠と法源は <c>Designer/seed/005_transition_rates.sql</c> が持つ
/// （附則 52 ①・53 ① 一〜三。逐語は docs/research/2026-09-11_インボイス経過措置の附則条文.md）。</para>
/// </remarks>
public static class StatutoryData
{
    /// <summary>
    /// 免税事業者等からの課税仕入れに係る経過措置の控除割合（<b>令和 8 年度改正後</b>）。
    /// </summary>
    /// <remarks>
    /// <b>経過措置の期間の外には行が無い</b>（2023-09-30 以前と 2031-10-01 以後）——
    /// <b>終わったことは「行が無いこと」で表す</b>（ddl/014・docs/11 §5-1）。
    /// </remarks>
    public static IReadOnlyList<TransitionalDeductionRate> TransitionalDeductionRates { get; } = Delivered();

    /// <summary>
    /// <b>令和 8 年度改正前</b>の控除割合。改正で過去の仕訳の計算結果が変わらないこと（I-16）の検証に使う。
    /// </summary>
    /// <remarks>
    /// <b>これだけは書き写しである。</b> 改正前の姿は<b>いまの表には入っていない</b>（配るのは改正後だけ）ので、
    /// 読む先が無い。<b>2029-10-01 以後の行を置かない</b>のは、改正後の表と同じ理由である
    /// ——終わったことは行の不在で表す。
    /// </remarks>
    public static IReadOnlyList<TransitionalDeductionRate> TransitionalDeductionRatesBeforeAmendment { get; } =
    [
        Rate("2023-10-01", "2026-09-30", 0.80m),
        Rate("2026-10-01", "2029-09-30", 0.50m),
    ];

    public static EffectiveDatedRuleSet<TransitionalDeductionRate> TransitionalDeductionRuleSet()
        => new(TransitionalDeductionRates);

    public static EffectiveDatedRuleSet<TransitionalDeductionRate> TransitionalDeductionRuleSetBeforeAmendment()
        => new(TransitionalDeductionRatesBeforeAmendment);

    /// <summary>配っている行を読んで、ドメインの型へ移す。</summary>
    /// <remarks>
    /// <b>読み出しの本体は <c>TransitionRuleLoader</c>（サーバ側）である。</b>
    /// ここが同じことをしているのは、<b>ドメインのテストがサーバ側の部品を参照できない</b>からで、
    /// <b>移し方（百分率 → 小数）が食い違えば、あちらのテストが赤くなる</b>。
    /// </remarks>
    private static IReadOnlyList<TransitionalDeductionRate> Delivered()
    {
        using var db = TestDatabase.CreateWithSeed();

        return TestDatabase
            .Query(
                db,
                "SELECT valid_from || '\t' || valid_to || '\t' || rate_percent || '\t' || version"
                + " FROM transition_purchase_rates ORDER BY valid_from")
            .Select(row => row.Split('\t'))
            .Select(fields => new TransitionalDeductionRate(
                new EffectivePeriod(
                    DateOnly.Parse(fields[0], CultureInfo.InvariantCulture),
                    DateOnly.Parse(fields[1], CultureInfo.InvariantCulture)),
                int.Parse(fields[2], CultureInfo.InvariantCulture) / 100m,
                new RuleVersion(fields[3])))
            .ToList();
    }

    private static TransitionalDeductionRate Rate(string from, string to, decimal ratio)
    {
        var period = new EffectivePeriod(
            DateOnly.Parse(from, CultureInfo.InvariantCulture),
            DateOnly.Parse(to, CultureInfo.InvariantCulture));
        return new TransitionalDeductionRate(period, ratio, new RuleVersion($"pre-amendment:{from}"));
    }
}
