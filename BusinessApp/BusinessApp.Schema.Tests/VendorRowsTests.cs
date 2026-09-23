namespace BusinessApp.Schema.Tests;

using System.Text.RegularExpressions;

using BusinessApp.TestSupport;

/// <summary>
/// <b>ベンダーが配る行の同値検査そのものを検査する</b>（<c>self-review</c> スキル §9）。
/// </summary>
/// <remarks>
/// <para><b>壊れた検査は赤で止まらない。</b> 差を 1 件も出さない実装は「一致」と言い、
/// <b>流した本人には分からない</b>。ここが、この網の赤くなる経路を固定する。</para>
/// <para>実際の突き合わせ（正典 ↔ 再生・正典 ↔ 稼働 DB）は
/// <see cref="MigrationEquivalenceTests.ベンダーが配る行も再生と正典で一致する"/> と
/// <c>migrate.ps1 -Verify</c> が行う。</para>
/// </remarks>
public class VendorRowsTests
{
    /// <summary>
    /// 初期データを入れる表は、<b>「ベンダーが配る」か「利用者が編集する」かのどちらかに必ず属する。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>両側から守る</b>（<c>self-review</c> スキル §9 の 2）。
    /// 一覧に書いた名前が実在するかだけを見ても、<b>行まで配る表を作って載せ忘れた回</b>は 1 件も鳴らない——
    /// <b>その表の行は誰も見ないまま緑になる</b>。</para>
    /// <para><b>母数は <c>Designer/seed/</c> が実際に INSERT している表から採る</b>。
    /// <b>どちらの一覧にも無い表を見つけたら、決めてから足す。</b></para>
    /// </remarks>
    [Fact]
    public void 初期データの表はベンダーの側か利用者の側のどちらかに属する()
    {
        Assert.Equal(
            SeededTables(),
            VendorRows.Tables.Concat(UserEditedTables).OrderBy(name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>初期値はベンダーが配るが、その後は利用者のものになる表。</b> 行の同値検査の対象にしない。
    /// </summary>
    /// <remarks>
    /// <b>税区分はここに居るが、理由が他と違う</b>——「利用者のもの」だからではなく、
    /// <b>編集させるか自体が未決</b>だからである（docs/12 の税区分の行・docs/04 §5）。
    /// </remarks>
    private static readonly string[] UserEditedTables =
    [
        "company_profile", "fiscal_years", "accounting_periods", "journal_entry_sequences",
        "departments", "tax_categories", "accounts",
    ];

    /// <summary><c>Designer/seed/</c> が行を入れている表（重複なし・名前順）。</summary>
    private static IEnumerable<string> SeededTables()
        => TestDatabase.SeedFiles()
            .SelectMany(file => Regex
                .Matches(File.ReadAllText(file), @"INSERT\s+INTO\s+(\w+)")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

    /// <summary>
    /// <b>比べるのは意味の列だけ。</b> 代理キーと監査列は、入れた順や時刻で変わる。
    /// </summary>
    [Fact]
    public void 代理キーと監査列は比べない()
    {
        using var db = TestDatabase.Create();

        Assert.Equal(
            ["valid_from", "valid_to", "rate_percent", "version", "legal_basis", "source_url", "confirmed_on"],
            VendorRows.MeaningfulColumns(db, "transition_purchase_rates"));
    }

    /// <summary>
    /// <b>同じ行でも <c>id</c> が違えば差になる、という形にしない。</b>
    /// </summary>
    /// <remarks>
    /// <b>新しく作った DB は 1 から、配達で足した DB は続きから <c>id</c> を振る。</b>
    /// そこを比べると、中身が同じでも毎回ずれる——<b>毎回赤くなる網は、無視される網である</b>。
    /// </remarks>
    [Fact]
    public void 識別子が違っても中身が同じなら差は出ない()
    {
        using var canonical = TestDatabase.CreateWithSeed();
        using var shifted = TestDatabase.CreateWithSeed();

        // 配達で足した DB を真似て、識別子を後ろへずらす。
        TestDatabase.Execute(shifted, "UPDATE transition_purchase_rates SET id = id + 100;");

        Assert.Empty(VendorRows.Diff(
            VendorRows.Dump(canonical), VendorRows.Dump(shifted), "正典", "ずらした側"));
    }

    /// <summary>
    /// <b>値が 1 つ違えば、両側から見た差として出る。</b>
    /// </summary>
    /// <remarks>
    /// <b>これが守りたいものそのものである</b>——稼働 DB の控除割合が正典と違えば、仕訳の税額が狂う。
    /// </remarks>
    [Fact]
    public void 控除割合が違えば差として出る()
    {
        using var canonical = TestDatabase.CreateWithSeed();
        using var broken = TestDatabase.CreateWithSeed();

        TestDatabase.Execute(
            broken,
            "UPDATE transition_purchase_rates SET rate_percent = 79"
            + " WHERE version = 'transition_purchase_rate:2023-10-01';");

        var diff = VendorRows.Diff(VendorRows.Dump(canonical), VendorRows.Dump(broken), "正典", "稼働 DB");

        // **数で釘付けしない。どちらに何があるかを字で並べる**（`self-review` スキル §9 の 6）。
        Assert.Equal(
            ["正典 に無い: " + RowOf("2023-10-01", "2026-09-30", 79, "附則 52 ①"),
             "稼働 DB に無い: " + RowOf("2023-10-01", "2026-09-30", 80, "附則 52 ①")],
            diff.OrderBy(line => line, StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>行が消えても差として出る。</b>
    /// </summary>
    [Fact]
    public void 行が足りなければ差として出る()
    {
        using var canonical = TestDatabase.CreateWithSeed();
        using var broken = TestDatabase.CreateWithSeed();

        TestDatabase.Execute(
            broken,
            "DELETE FROM transition_purchase_rates WHERE version = 'transition_purchase_rate:2030-10-01';");

        Assert.Equal(
            ["稼働 DB に無い: transition_purchase_rates | valid_from=2030-10-01 | valid_to=2031-09-30"
             + " | rate_percent=30 | version=transition_purchase_rate:2030-10-01"
             + " | legal_basis=所税法等一部改正法（平成28年法律第15号）附則 53 ① 三（令和8年10月1日施行版）"
             + " | source_url=https://laws.e-gov.go.jp/law/363AC0000000108 | confirmed_on=2026-09-10"],
            VendorRows.Diff(VendorRows.Dump(canonical), VendorRows.Dump(broken), "正典", "稼働 DB"));
    }

    /// <summary>
    /// <b>行が 1 つも無いことを「一致」と言わない。</b>
    /// </summary>
    /// <remarks>
    /// <b>差は 0 件でも、それは「問題なし」ではなく「見ていない」である</b>
    /// （<c>self-review</c> スキル §9 の 1）。<b>空回りの側から言わせる。</b>
    /// </remarks>
    [Fact]
    public void 行が1つも無ければ空回りとして報告する()
    {
        using var empty = TestDatabase.Create();

        var rows = VendorRows.Dump(empty);

        Assert.Empty(rows);
        Assert.Empty(VendorRows.Diff(rows, VendorRows.Dump(empty), "正典", "稼働 DB"));
        Assert.Equal(
            ["行が 1 つも取れていない表がある: transition_purchase_rates tax_rates"],
            VendorRows.Vacuous(rows));
    }

    /// <summary>行が取れていれば空回りとは言わない。</summary>
    [Fact]
    public void 行が取れていれば空回りとは言わない()
    {
        using var seeded = TestDatabase.CreateWithSeed();

        Assert.Empty(VendorRows.Vacuous(VendorRows.Dump(seeded)));
    }

    /// <summary>
    /// <b>監査列を NULL にしても、行は取れたままである。</b>
    /// </summary>
    /// <remarks>
    /// <b>監査列は比べないので、NULL にしても取り出しは 1 行も減ってはならない</b>——
    /// 減るということは、比べる列の選び方が間違っているということである。
    /// </remarks>
    [Fact]
    public void 監査列がNULLでも行は取れる()
    {
        using var db = TestDatabase.CreateWithSeed();
        var before = VendorRows.Dump(db);

        TestDatabase.Execute(db, "UPDATE transition_purchase_rates SET updater = NULL, created_at = NULL;");

        Assert.Equal(before, VendorRows.Dump(db));
    }

    /// <summary>
    /// <b>NULL は印を付けて残す。</b> 連結ごと NULL にして行を消さない。
    /// </summary>
    /// <remarks>
    /// <c>COALESCE</c> を書き落とすと、<b>NULL を含む行は連結ごと NULL になって行そのものが取れなくなる</b>
    /// ——差ではなく「行が消えた」に化ける。
    /// <b>2026-09-23 まで、この枝は本番の表では一度も通らなかった</b>（比べる列が全部 NOT NULL だった）が、
    /// <b>税率の表の <c>valid_to</c> が NULL 可になり、配る 3 行すべてが NULL になった</b>
    /// ——いまは本物の表で毎回通る。
    /// </remarks>
    [Fact]
    public void NULLは印を付けて残す()
    {
        using var db = TestDatabase.CreateWithSeed();

        Assert.All(
            VendorRows.Dump(db).Where(row => row.StartsWith("tax_rates |", StringComparison.Ordinal)),
            row => Assert.Contains("valid_to=(null)", row, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>報告そのもの</b>——出る行と終了コードを字で固定する。
    /// </summary>
    /// <remarks>
    /// <b>判定をどれだけ厳しく当てても、印字する側がそれを捨てれば誰も気づかない</b>
    /// （<c>self-review</c> スキル §9 の 5）。<b>実行ファイルはこれを印字するだけ</b>なので、
    /// ここが本番の出力の正典である。
    /// </remarks>
    [Fact]
    public void 一致したときの報告は表の名前を名乗る()
    {
        using var db = TestDatabase.CreateWithSeed();
        var rows = VendorRows.Dump(db);

        var (exitCode, lines) = VendorRows.Report(rows, rows);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            ["一致: ベンダーが配る行は正典と同値である（transition_purchase_rates・tax_rates。7 行）。"],
            lines);
    }

    /// <summary>ずれたときは、件数と差の字を出して 1 で終わる。</summary>
    [Fact]
    public void ずれたときの報告は差の字を出す()
    {
        using var canonical = TestDatabase.CreateWithSeed();
        using var broken = TestDatabase.CreateWithSeed();

        TestDatabase.Execute(
            broken,
            "DELETE FROM transition_purchase_rates WHERE version = 'transition_purchase_rate:2030-10-01';");

        var (exitCode, lines) = VendorRows.Report(VendorRows.Dump(canonical), VendorRows.Dump(broken));

        Assert.Equal(1, exitCode);
        Assert.Equal(
            ["行のずれが 1 件ある。",
             "稼働 DB に無い: " + RowOf("2030-10-01", "2031-09-30", 30, "附則 53 ① 三（令和8年10月1日施行版）")],
            lines);
    }

    /// <summary>
    /// <b>空回りは「一致」ではなく 1 で終わる。</b>
    /// </summary>
    /// <remarks>
    /// <b>差が 0 件であることと、見たことは違う</b>（<c>self-review</c> スキル §9 の 1）。
    /// </remarks>
    [Fact]
    public void 行が取れていないときの報告は一致と言わない()
    {
        using var empty = TestDatabase.Create();
        var rows = VendorRows.Dump(empty);

        var (exitCode, lines) = VendorRows.Report(rows, rows);

        Assert.Equal(1, exitCode);
        Assert.Equal(["行が 1 つも取れていない表がある: transition_purchase_rates tax_rates"], lines);
    }

    /// <summary>期待する行の字。</summary>
    private static string RowOf(string from, string to, int percent, string basis)
        => $"transition_purchase_rates | valid_from={from} | valid_to={to} | rate_percent={percent}"
            + $" | version=transition_purchase_rate:{from}"
            + $" | legal_basis=所税法等一部改正法（平成28年法律第15号）{basis}"
            + " | source_url=https://laws.e-gov.go.jp/law/363AC0000000108 | confirmed_on=2026-09-10";
}
