namespace BusinessApp.AccountingCore.Server.Tests.ConsumptionTax.Infrastructure;

using System.Globalization;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Server.ConsumptionTax.Infrastructure;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.TestSupport;

/// <summary>
/// 制度ルール（税率）の読み出し（ADR-0069・docs/11 §1-1）。
/// </summary>
/// <remarks>
/// <para><b>表・配った行・読み出し・引き当てのどれが欠けても、ここが赤くなる。</b>
/// <b>ただし「仕訳の税額」まではまだ届いていない</b>——税行を作る側も、
/// <c>journal_lines.applied_rule_version</c> に書く側も、フェーズ 3 のこの先である。</para>
/// <para><b>期待する値を書き下す。</b> 読んだ値を読んだ値と比べる形にすると、
/// <b>行を書き換えても期待値が一緒に動いて釣り合う</b>（<c>self-review</c> スキル §9 の 4）。
/// <b>条文の数字そのものを検体に置く</b>——出どころは
/// <c>Designer/seed/006_tax_rates.sql</c> が引いている消税法 29 と地方税法 72 の 83 である。</para>
/// </remarks>
public class TaxRateLoaderTests
{
    private static TaxRateLoader LoaderOf(AccountingServer server)
        => new(server.Accessor, SqliteDbAccessor.DataSourceName);

    /// <summary>
    /// <b>初期データの 2 本が、条文どおりの区分・期間・率で読める。</b>
    /// </summary>
    /// <remarks>
    /// <b>数ではなく「どの区分がいつから何か」の組を並べる</b>（<c>self-review</c> スキル §9 の 6）。
    /// <b>終期は 2 本とも無い</b>——消税法 29 も地方税法 72 の 83 も期限を書いていない。
    /// </remarks>
    [Fact]
    public async Task 初期データの税率は条文どおりに読める()
    {
        using var server = new AccountingServer();

        var book = await LoaderOf(server).LoadAsync();

        Assert.Equal(
            [
                (TaxRateKind.Standard, "2019/10/01", (DateOnly?)null, 780, 22, 78, "tax_rate:standard:2019-10-01"),
                (TaxRateKind.Reduced, "2019/10/01", null, 624, 22, 78, "tax_rate:reduced:2019-10-01"),
            ],
            book.Rules.Select(rate => (
                rate.Kind,
                rate.Period.From.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
                rate.Period.To,
                rate.NationalRatePer10000,
                rate.LocalNumerator,
                rate.LocalDenominator,
                rate.Version.Value)));
    }

    /// <summary>
    /// <b>同じ区分に 2 つの期間がある行を読んで、日付でどちらが当たるかを決める。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>配った 2 行だけでは、この経路を 1 度も通らない</b>——2 行とも期間が同じなので、
    /// <c>DbValue.ToNullableDate</c> の<b>非 NULL の枝</b>も、
    /// <c>EffectiveDatedRuleSet.ResolveAt</c> の<b>複数から選ぶ側</b>も動かない。</para>
    /// <para><b>守りを外さずに入れられる。</b> 2019-09-30 で閉じた期間は、
    /// 2019-10-01 から始まる終期なしの行と重ならない（<c>Designer/ddl/015</c> の重なりのトリガ）。
    /// <b>正規の経路で作れる検体は、正規の経路で作る。</b></para>
    /// <para><b>数は「2019-09-30 まで施行されていた版」の 6.3% ＋ 63 分の 17 を使う</b>
    /// ——<b>足す行を配った <c>standard</c> の行と同じ値にすると、引き分けを取り違えても緑のまま通る</b>。</para>
    /// </remarks>
    [Theory]
    [InlineData("2019-09-30", 630, 17, 63)]
    [InlineData("2019-10-01", 780, 22, 78)]
    public async Task 同じ区分に2つの期間があれば日付でどちらが当たるかが決まる(
        string taxPoint, int national, int numerator, int denominator)
    {
        using var server = new AccountingServer();
        server.Execute(
            "INSERT INTO tax_rates"
            + " (rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator,"
            + " local_denominator, version, legal_basis, source_url, confirmed_on)"
            + " VALUES ('standard', '2014-04-01', '2019-09-30', 630, 17, 63,"
            + " 'tax_rate:standard:2014-04-01', '検体', '検体', '2026-09-23');");

        var book = await LoaderOf(server).LoadAsync();
        var resolved = book.ResolveAt(
            TaxRateKind.Standard, DateOnly.Parse(taxPoint, CultureInfo.InvariantCulture));

        Assert.NotNull(resolved);
        Assert.Equal(
            (national, numerator, denominator),
            (resolved.NationalRatePer10000, resolved.LocalNumerator, resolved.LocalDenominator));
        Assert.Equal(
            [new DateOnly(2019, 9, 30), null],
            book.Rules.Where(rate => rate.Kind == TaxRateKind.Standard).Select(rate => rate.Period.To));
    }

    /// <summary>
    /// <b>区分と日付で引ける。終期が無いので、未来の日でも引ける。</b>
    /// </summary>
    /// <remarks>
    /// <b>切り替わりの両側を撃つ。</b> 片側だけだと、期間を 1 日ずらしても緑のまま通る
    /// （qa/03 の L-61 の型）。
    /// <b>2019-09-30 以前の行は配っていない</b>ので、その日は引けない
    /// （いつから 6.3% かを確かめていない。税率リサーチ §3）。
    /// </remarks>
    [Theory]
    [InlineData("2019-09-30", null)]
    [InlineData("2019-10-01", 780)]
    [InlineData("2026-09-23", 780)]
    [InlineData("2099-12-31", 780)]
    public async Task 標準税率は配った期間の中だけで引ける(string taxPoint, int? expected)
    {
        using var server = new AccountingServer();

        var book = await LoaderOf(server).LoadAsync();
        var resolved = book.ResolveAt(
            TaxRateKind.Standard, DateOnly.Parse(taxPoint, CultureInfo.InvariantCulture));

        if (expected is null)
        {
            Assert.Null(resolved);
            return;
        }

        Assert.NotNull(resolved);
        Assert.Equal(expected.Value, resolved.NationalRatePer10000);
    }

    /// <summary>
    /// <b>仕訳に写した版で引き直せる</b>（I-16）。
    /// </summary>
    [Fact]
    public async Task 版で引き直せる()
    {
        using var server = new AccountingServer();

        var book = await LoaderOf(server).LoadAsync();
        var resolved = book.ResolveByVersion(new RuleVersion("tax_rate:reduced:2019-10-01"));

        Assert.NotNull(resolved);
        Assert.Equal(TaxRateKind.Reduced, resolved.Kind);
        Assert.Equal(624, resolved.NationalRatePer10000);
    }

    /// <summary>
    /// <b>同じ区分で期間が重なる行を読んだら、組み立ての時点で落ちる。</b>
    /// </summary>
    /// <remarks>
    /// <b>DB のトリガも重なりを止めているが、そちらを迂回して入った行を読む経路はありうる</b>
    /// （取込・直打ち）。<b>読み出しの側でも止める</b>——
    /// 重なりを許すと「どちらが当たったか」が実行順に依存し、<b>静かに違う税率で計算される</b>。
    /// <b>配っている行は終期が無いので、後ろに足す行はどれも重なる。</b>
    /// </remarks>
    [Fact]
    public async Task 同じ区分で期間が重なる行は組み立てで落ちる()
    {
        using var server = new AccountingServer();

        // **DB の守りを外してから入れる**（TransitionRuleLoaderTests と同じ形）。
        // 許可表（TestDatabase.RemovableTriggers）を通し、**終わったら必ず貼り直す**。
        server.WithoutTrigger(
            "trg_tax_rates_no_overlap_insert",
            "INSERT INTO tax_rates"
            + " (rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator,"
            + " local_denominator, version, legal_basis, source_url, confirmed_on)"
            // **780 のままにする。** 合計税率が万分率の整数になる組でないと CHECK が先に断り、
            // **重なりではない理由で落ちる**（22/78 のとき国税は 39 の倍数しか入らない）。
            + " VALUES ('standard', '2026-10-01', NULL, 780, 22, 78,"
            + " 'tax_rate:standard:2026-10-01', '検体', '検体', '2026-09-23');");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => LoaderOf(server).LoadAsync());

        Assert.Contains("制度ルールの有効期間が重複している", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>知らない税率区分の行は、読み出しで表と列を名乗って落ちる</b>——黙って定義の外の値にしない。
    /// </summary>
    /// <remarks>
    /// <b>CHECK を一時的に効かなくして入れる</b>（取込・直打ち、0045 を当てていない DB の形）。<c>'legacy_8'</c> は 2026-09-24 にスコープの外にした区分、
    /// <c>'2'</c> は外した列挙子の番号——<b>数字の字を名前として受ける読み方だと、<c>(TaxRateKind)2</c> が黙って通る</b>。
    /// </remarks>
    [Theory]
    [InlineData("legacy_8")]
    [InlineData("2")]
    public async Task 知らない税率区分の行は読み出しで落ちる(string kind)
    {
        using var server = new AccountingServer();
        server.Execute(
            "PRAGMA ignore_check_constraints = ON;"
            + " INSERT INTO tax_rates"
            + " (rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator,"
            + " local_denominator, version, legal_basis, source_url, confirmed_on)"
            + $" VALUES ('{kind}', '2019-10-01', NULL, 630, 17, 63,"
            + $" 'tax_rate:{kind}:2019-10-01', '検体', '検体', '2026-09-25');"
            + " PRAGMA ignore_check_constraints = OFF;");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => LoaderOf(server).LoadAsync());

        Assert.Equal($"tax_rates の rate_kind に知らない値がある: {kind}", exception.Message);
    }
}
