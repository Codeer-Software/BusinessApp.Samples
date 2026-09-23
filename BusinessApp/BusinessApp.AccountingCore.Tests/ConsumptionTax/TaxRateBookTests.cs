namespace BusinessApp.AccountingCore.Tests.ConsumptionTax;

using System.Globalization;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Tests.Fixtures;

/// <summary>
/// 税率の引き当て（docs/11 §1-1）。<b>区分と日付で 1 本選ぶ。</b>
/// </summary>
/// <remarks>
/// <para><b>1 行の意味は <see cref="TaxRateTests"/></b> が見る。ここは<b>選び方</b>だけを見る。</para>
/// <para><b>配っている 3 行は区分だけが違い、期間は全部同じ</b>（2019-10-01 から終期なし）なので、
/// <b>「同じ区分に 2 つの期間がある本」を配った行だけでは作れない</b>。
/// <b>その形は自分で組み立てて撃つ</b>——次の税率改正の日に初めて火が入る経路である。</para>
/// </remarks>
public class TaxRateBookTests
{
    private static readonly TaxRateBook Delivered = StatutoryData.TaxRateBook();

    /// <summary>
    /// <b>3 区分が同じ日に同時に引ける。</b>
    /// </summary>
    /// <remarks>
    /// <b>区分ごとに期間の集合を分ける根拠がここである</b>（<see cref="TaxRateBook"/>）。
    /// 3 区分を 1 つの <see cref="EffectiveDatedRuleSet{TRule}"/> にまとめると、
    /// <b>重なりとして構築時に投げる</b>——配っている 3 行はすべて 2019-10-01 から始まる。
    /// </remarks>
    [Fact]
    public void 同じ日に3区分とも引ける()
    {
        var date = new DateOnly(2026, 4, 1);

        Assert.Equal(
            [780, 624, 630],
            new[] { TaxRateKind.Standard, TaxRateKind.Reduced, TaxRateKind.Legacy8 }
                .Select(kind => Delivered.ResolveAt(kind, date)!.NationalRatePer10000));
    }

    /// <summary>
    /// <b>同じ区分に 2 つの期間があるとき、日付でどちらの行が当たるかが決まる。</b>
    /// </summary>
    /// <remarks>
    /// <b>配った行だけでは一度も通らない経路である</b>——3 行とも期間が同じなので、
    /// <c>EffectiveDatedRuleSet.ResolveAt</c> の「複数から選ぶ」側に入らない。
    /// <b>税率が改正された日に初めて火が入る</b>ので、いま組み立てて撃っておく。
    /// <b>切り替わりの両側を撃つ</b>——片側だけだと、期間を 1 日ずらしても緑のまま通る（qa/03 L-61）。
    /// </remarks>
    [Theory]
    [InlineData("2019-09-30", 630)]
    [InlineData("2019-10-01", 780)]
    [InlineData("2099-12-31", 780)]
    [InlineData("2014-03-31", null)]
    public void 同じ区分に2つの期間があるとき日付でどちらが当たるかが決まる(string taxPoint, int? expected)
    {
        var book = new TaxRateBook([Past, Current]);

        var resolved = book.ResolveAt(
            TaxRateKind.Standard, DateOnly.Parse(taxPoint, CultureInfo.InvariantCulture));

        Assert.Equal(expected, resolved?.NationalRatePer10000);
    }

    /// <summary><b>全件は区分順・期間順に並ぶ。</b></summary>
    /// <remarks>
    /// <b>期間で並べていないと、上の引き分けが「先に入った行」に依存する</b>
    /// （<see cref="EffectiveDatedRuleSet{TRule}"/> は自分でも並べ替えるが、全件を返す
    /// <see cref="TaxRateBook.Rules"/> は本が持つ）。<b>入れる順を逆にして撃つ。</b>
    /// </remarks>
    [Fact]
    public void 全件は区分順と期間順に並ぶ()
    {
        var book = new TaxRateBook([Current, Past, Reduced]);

        Assert.Equal(
            [
                (TaxRateKind.Standard, new DateOnly(2014, 4, 1)),
                (TaxRateKind.Standard, new DateOnly(2019, 10, 1)),
                (TaxRateKind.Reduced, new DateOnly(2019, 10, 1)),
            ],
            book.Rules.Select(rate => (rate.Kind, rate.Period.From)));
    }

    /// <summary>
    /// <b>配った期間より前は引けない。</b>
    /// </summary>
    /// <remarks>
    /// <b>null は「税率 0%」ではない</b>——制度ルールが無い日の取引は計算できないという意味で、
    /// 呼ぶ側は 0 円を返さずに断る（docs/11 §1-1）。
    /// <b>配っているのは 2019-10-01 からの行だけである</b>（それより前は、いつから 6.3% かを
    /// 確かめていない。税率リサーチ §3）。
    /// </remarks>
    [Theory]
    [InlineData("2019-09-30")]
    [InlineData("2015-01-01")]
    public void 配った期間より前は引けない(string taxPoint)
    {
        Assert.Null(
            Delivered.ResolveAt(TaxRateKind.Standard, DateOnly.Parse(taxPoint, CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// <b>終期が無いので、遠い未来でも引ける。</b>
    /// </summary>
    /// <remarks>
    /// 消税法 29 も地方税法 72 の 83 も期限を書いていない（税率リサーチ §1）ので、
    /// <b>標準税率と軽減税率の行には終期を置いていない</b>
    /// ——経過措置の控除割合（<see cref="TransitionalDeductionRateTests"/>）とはここが逆である。
    /// <b><c>legacy_8</c> の終期なしは Claude の当てはめで、開発者は未承認である</b>（docs/05 の Q-54）。
    /// </remarks>
    [Fact]
    public void 終期が無いので未来の日でも引ける()
    {
        var rate = Delivered.ResolveAt(TaxRateKind.Standard, new DateOnly(2099, 12, 31));

        Assert.NotNull(rate);
        Assert.Null(rate.Period.To);
    }

    /// <summary>
    /// <b>版で引ける。</b> 過去の仕訳は、日付ではなく保存された版で再現する（I-16）。
    /// </summary>
    [Fact]
    public void 版で引ける()
    {
        var rate = Delivered.ResolveByVersion(new RuleVersion("tax_rate:legacy_8:2019-10-01"));

        Assert.NotNull(rate);
        Assert.Equal(TaxRateKind.Legacy8, rate.Kind);
        Assert.Equal(630, rate.NationalRatePer10000);
    }

    /// <summary>知らない版では引けない。</summary>
    [Fact]
    public void 知らない版では引けない()
    {
        Assert.Null(Delivered.ResolveByVersion(new RuleVersion("tax_rate:standard:2030-10-01")));
    }

    /// <summary>
    /// <b>行が 1 本も無い区分は、日を問わず引けない。</b>
    /// </summary>
    /// <remarks>
    /// <b>「その区分の行が無い」と「その日の行が無い」は別である</b>が、
    /// <b>呼ぶ側にとっては同じ——どちらも計算できない</b>ので、同じ null を返す。
    /// <b>区分を足して行を配り忘れると、ここを通る</b>（docs/05 の Q-54）。
    /// </remarks>
    [Fact]
    public void 行が1本も無い区分は引けない()
    {
        var onlyStandard = new TaxRateBook([Current]);

        Assert.NotNull(onlyStandard.ResolveAt(TaxRateKind.Standard, new DateOnly(2026, 4, 1)));
        Assert.Null(onlyStandard.ResolveAt(TaxRateKind.Reduced, new DateOnly(2026, 4, 1)));
    }

    /// <summary>
    /// <b>同じ区分で期間が重なる行は、読み出し側も拒む。</b>
    /// </summary>
    /// <remarks>
    /// <b>守りは 2 枚である</b>（ADR-0069）——DB のトリガと、ここ。
    /// <b>取込や直打ちで重なる行が入りうる</b>ので、読み出し側も自分で確かめる。
    /// <b>配っている行は終期が無いので、後ろに足す行はどれも重なる。</b>
    /// </remarks>
    [Fact]
    public void 同じ区分で期間が重なる行は拒む()
    {
        var later = RateOf(TaxRateKind.Standard, "2026-10-01", null, 780);

        var exception = Assert.Throws<ArgumentException>(() => new TaxRateBook([Current, later]));

        Assert.Contains("制度ルールの有効期間が重複している", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>区分が違えば、期間が重なってもよい。</b>
    /// </summary>
    /// <remarks>
    /// 上の検査を「全件で重なりを見る」に書き換えると、ここだけが赤くなる——
    /// <b>配っている 3 行はすべて 2019-10-01 から始まる</b>。
    /// </remarks>
    [Fact]
    public void 区分が違えば期間が重なってもよい()
    {
        Assert.Equal(
            [
                (TaxRateKind.Standard, new DateOnly(2019, 10, 1), (DateOnly?)null),
                (TaxRateKind.Reduced, new DateOnly(2019, 10, 1), null),
                (TaxRateKind.Legacy8, new DateOnly(2019, 10, 1), null),
            ],
            Delivered.Rules.Select(rate => (rate.Kind, rate.Period.From, rate.Period.To)));
    }

    // **検体の行。** **`Current` と `Reduced` は配っている行と同じ値である**——
    // 「配っている 3 行と同じ引き方になる」ことを見るためで、**そこは同じでよい**。
    // **`Past` だけは違う値にする**（6.3% ＋ 63 分の 17。「2019-09-30 まで施行されていた版」の数）——
    // **どちらが当たるかを見る検体で 2 つが同じ値だと、取り違えても緑のまま通る**（qa/03 の縮退の型）。
    private static TaxRate Past => RateOf(TaxRateKind.Standard, "2014-04-01", "2019-09-30", 630, 17, 63);

    private static TaxRate Current => RateOf(TaxRateKind.Standard, "2019-10-01", null, 780);

    private static TaxRate Reduced => RateOf(TaxRateKind.Reduced, "2019-10-01", null, 624);

    private static TaxRate RateOf(
        TaxRateKind kind, string from, string? to, int national, int numerator = 22, int denominator = 78)
        => new(
            kind,
            new EffectivePeriod(
                DateOnly.Parse(from, CultureInfo.InvariantCulture),
                to is null ? null : DateOnly.Parse(to, CultureInfo.InvariantCulture)),
            national,
            numerator,
            denominator,
            new RuleVersion($"tax_rate:{kind}:{from}"));
}
