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

    /// <summary>税率（<c>ddl/015</c> ＋ <c>seed/006</c>）。<b>配っている行そのものを読む。</b></summary>
    /// <remarks>
    /// <b>国税は万分率の整数、地方は分数のまま持つ</b>（docs/11 §1-1）。
    /// <b>ここで小数へ直さない</b>——78 分の 22 は小数で終わらず、直した時点で丸めが入る。
    /// </remarks>
    public static IReadOnlyList<TaxRate> TaxRates { get; } = DeliveredTaxRates();

    public static EffectiveDatedRuleSet<TransitionalDeductionRate> TransitionalDeductionRuleSet()
        => new(TransitionalDeductionRates);

    public static TaxRateBook TaxRateBook() => new(TaxRates);

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

    /// <summary>配っている税率の行を読んで、ドメインの型へ移す。</summary>
    /// <remarks>
    /// <para><b>読み出しの本体は <c>TaxRateLoader</c>（サーバ側）である</b>
    /// （<see cref="Delivered"/> と同じ事情——ドメインのテストはサーバ側の部品を参照しない）。
    /// <b>だから「移し方が食い違えば、あちらのテストが赤くなる」とは言えない。</b>
    /// 両者を突き合わせているのは、<b>2 つのテストが同じ数字を別々に書いた表明</b>である
    /// （<c>TaxRateTests</c> と <c>TaxRateLoaderTests</c>）。</para>
    /// <para><b>だから、DDL が通す形はこちらも読めなければならない。</b>
    /// 日付は先頭 10 文字を <c>ParseExact</c> で読む——<b>CLB は日付の列へ
    /// <c>"2019-10-01 00:00:00"</c> と時刻付きで書き、DDL はその形を通す</b>ので、
    /// 素の <c>DateOnly.Parse</c> だと本番が通す行で落ちる。</para>
    /// <para><b>終期なしの印は <c>(null)</c> にそろえる</b>（<see cref="VendorRows"/> と同じ字）。
    /// 空文字にすると、<b>「終期なし」と「空文字が入った行」が同じ字になる</b>——
    /// 空文字はいま日付のトリガが断るが、<b>トリガを 1 本外した回（制約ノックアウト）には入る</b>。</para>
    /// </remarks>
    private static IReadOnlyList<TaxRate> DeliveredTaxRates()
    {
        using var db = TestDatabase.CreateWithSeed();

        return TestDatabase
            .Query(
                db,
                "SELECT rate_kind || '	' || valid_from || '	' || COALESCE(valid_to, '(null)')"
                + " || '	' || national_rate_per_10000 || '	' || local_numerator"
                + " || '	' || local_denominator || '	' || version"
                + " FROM tax_rates ORDER BY rate_kind, valid_from")
            .Select(row => row.Split('	'))
            .Select(fields => new TaxRate(
                KindOf(fields[0]),
                new EffectivePeriod(DayOf(fields[1])!.Value, DayOf(fields[2])),
                int.Parse(fields[3], CultureInfo.InvariantCulture),
                int.Parse(fields[4], CultureInfo.InvariantCulture),
                int.Parse(fields[5], CultureInfo.InvariantCulture),
                new RuleVersion(fields[6])))
            .ToList();
    }

    /// <summary>区分値の字を列挙子に直す。<b>知らない字は投げる。</b></summary>
    /// <remarks>
    /// <b>ここに書いた対応が正しいことは <c>EnumConsistencyTests</c> が別に見ている</b>
    /// ——DDL の CHECK・CLB の enum・C# の列挙子の 3 者を突き合わせるので、
    /// <b>区分を 1 つ足してここを直し忘れれば、この <c>switch</c> が投げる</b>。
    /// </remarks>
    private static TaxRateKind KindOf(string value) => value switch
    {
        "standard" => TaxRateKind.Standard,
        "reduced" => TaxRateKind.Reduced,
        "legacy_8" => TaxRateKind.Legacy8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "知らない税率区分"),
    };

    /// <summary>日付の字を読む。<c>(null)</c> は「終期なし」。</summary>
    private static DateOnly? DayOf(string value) => value == "(null)"
        ? null
        : DateOnly.ParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TransitionalDeductionRate Rate(string from, string to, decimal ratio)
    {
        var period = new EffectivePeriod(
            DateOnly.Parse(from, CultureInfo.InvariantCulture),
            DateOnly.Parse(to, CultureInfo.InvariantCulture));
        return new TransitionalDeductionRate(period, ratio, new RuleVersion($"pre-amendment:{from}"));
    }
}
