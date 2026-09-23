namespace BusinessApp.AccountingCore.Server.Tests.ConsumptionTax.Infrastructure;

using BusinessApp.AccountingCore.Server.ConsumptionTax.Infrastructure;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.TestSupport;

/// <summary>
/// 制度ルール（経過措置の控除割合）の読み出し（ADR-0069）。
/// </summary>
/// <remarks>
/// <para><b>表・配った行・読み出し・引き当てのどれが欠けても、ここが赤くなる。</b>
/// <b>ただし「仕訳の税額」まではまだ届いていない</b>——税行を作る側も、
/// <c>journal_lines.applied_rule_version</c> に書く側も、フェーズ 3 のこの先である。</para>
/// <para><b>期待する割合を書き下す。</b> 読んだ値を読んだ値と比べる形（DB から取り直して突き合わせる）に
/// すると、<b>行を書き換えても期待値が一緒に動いて釣り合う</b>（<c>self-review</c> スキル §9 の 4）。
/// <b>条文の数字そのものを検体に置く</b>——
/// 出どころは <c>Designer/seed/005_transition_rates.sql</c> が引いている附則である。</para>
/// </remarks>
public class TransitionRuleLoaderTests
{
    private static TransitionRuleLoader LoaderOf(AccountingServer server)
        => new(server.Accessor, SqliteDbAccessor.DataSourceName);

    /// <summary>
    /// <b>初期データの 4 本が、条文どおりの期間と割合で読める。</b>
    /// </summary>
    /// <remarks>
    /// <b>数ではなく「どの期間が何 %」の組を並べる</b>（<c>self-review</c> スキル §9 の 6）。
    /// 本数だけを見ると、<b>別の期間に差し替えても緑のまま通る</b>。
    /// </remarks>
    [Fact]
    public async Task 初期データの控除割合は条文どおりに読める()
    {
        using var server = new AccountingServer();

        var rules = await LoaderOf(server).LoadDeductionRatesAsync();

        Assert.Equal(
            [
                ("2023/10/01", "2026/09/30", 0.8m, "transition_purchase_rate:2023-10-01"),
                ("2026/10/01", "2028/09/30", 0.7m, "transition_purchase_rate:2026-10-01"),
                ("2028/10/01", "2030/09/30", 0.5m, "transition_purchase_rate:2028-10-01"),
                ("2030/10/01", "2031/09/30", 0.3m, "transition_purchase_rate:2030-10-01"),
            ],
            rules.Rules.Select(rule => (
                rule.Period.From.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture),
                rule.Period.To!.Value.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture),
                rule.Ratio,
                rule.Version.Value)));
    }

    /// <summary>
    /// <b>境界の日で割合が切り替わる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>切り替わりの両側を撃つ。</b> 片側だけだと、期間を 1 日ずらしても緑のまま通る
    /// （qa/03 の L-61 の型）。</para>
    /// <para><b>2026-10-01 をまたぐ回は、フェーズ 3 の完了条件そのものである</b>（docs/04）。</para>
    /// </remarks>
    [Theory]
    [InlineData("2023-09-30", null)]          // 経過措置が始まる前
    [InlineData("2023-10-01", 0.8)]           // 五年施行日
    [InlineData("2026-09-30", 0.8)]           // 適用期限
    [InlineData("2026-10-01", 0.7)]           // 適用期限の翌日
    [InlineData("2028-09-30", 0.7)]
    [InlineData("2028-10-01", 0.5)]
    [InlineData("2030-09-30", 0.5)]
    [InlineData("2030-10-01", 0.3)]
    [InlineData("2031-09-30", 0.3)]           // 経過措置の最終日
    [InlineData("2031-10-01", null)]          // **終わった翌日。行が無いので引けない**
    public async Task 課税仕入れの日で引くと境界の両側で割合が変わる(string taxPoint, double? expected)
    {
        using var server = new AccountingServer();

        var rules = await LoaderOf(server).LoadDeductionRatesAsync();
        var resolved = rules.ResolveAt(DateOnly.Parse(taxPoint, System.Globalization.CultureInfo.InvariantCulture));

        if (expected is null)
        {
            // **0% の行を置かない**ので、経過措置の外は「引けない」で表す（ddl/014）。
            Assert.Null(resolved);
            return;
        }

        Assert.NotNull(resolved);
        Assert.Equal((decimal)expected.Value, resolved.Ratio);
    }

    /// <summary>
    /// <b>仕訳に写した版で引き直せる</b>（I-16）。
    /// </summary>
    /// <remarks>
    /// <b>過去を再計算しないための口である。</b> 日付で引くと、後から期間を直した回に
    /// <b>計上済みの仕訳の割合が変わる</b>。
    /// </remarks>
    [Fact]
    public async Task 版で引き直せる()
    {
        using var server = new AccountingServer();

        var rules = await LoaderOf(server).LoadDeductionRatesAsync();
        var resolved = rules.ResolveByVersion(new RuleVersion("transition_purchase_rate:2026-10-01"));

        Assert.NotNull(resolved);
        Assert.Equal(0.7m, resolved.Ratio);
        Assert.Equal(new DateOnly(2026, 10, 1), resolved.Period.From);
    }

    /// <summary>
    /// <b>期間が重なる行を読んだら、組み立ての時点で落ちる。</b>
    /// </summary>
    /// <remarks>
    /// <b>DB のトリガも重なりを止めているが、そちらを迂回して入った行を読む経路はありうる</b>
    /// （取込・直打ち）。<b>読み出しの側でも止める</b>——
    /// 重なりを許すと「どちらが当たったか」が実行順に依存し、<b>静かに違う割合で計算される</b>。
    /// </remarks>
    [Fact]
    public async Task 期間が重なる行は組み立てで落ちる()
    {
        using var server = new AccountingServer();

        // **DB の守りを外してから入れる。** トリガが効いている限り重なる行は入らない——
        // **だからこの検体は「トリガを迂回して入った行」を作るところから始める**
        // （取込・直打ちで入りうる形。守りは 2 つあり、ここで測るのは読み出し側の 1 つである）。
        //
        // **生の DROP TRIGGER を書かない。** 許可表（TestDatabase.RemovableTriggers）を通し、
        // **終わったら必ず貼り直す**——貼り直さないと、その接続の残りが
        // 「守りの無い DB」で緑になる。
        server.WithoutTrigger(
            "trg_transition_purchase_rates_no_overlap_insert",
            "INSERT INTO transition_purchase_rates"
            + " (valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)"
            + " VALUES ('2026-09-01', '2026-09-15', 70, 'transition_purchase_rate:2026-09-01',"
            + " '検体', '検体', '2026-09-10');");

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => LoaderOf(server).LoadDeductionRatesAsync());

        Assert.Contains("制度ルールの有効期間が重複している", exception.Message, StringComparison.Ordinal);
    }
}
