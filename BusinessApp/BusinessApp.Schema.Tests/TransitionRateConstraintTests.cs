namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 制度ルール（経過措置の控除割合）の表の守り（<c>Designer/ddl/014</c>・ADR-0069）。
/// </summary>
/// <remarks>
/// <para><b>この表には画面が無い。</b> 書き込む経路は <c>sql</c> CLI・取込・直打ちだけで、
/// <b>DDL が最後の砦になる</b>——<c>designcheck</c> も <c>lint_design.py</c> も、
/// モジュールが指していない表には 1 つも当たらない。</para>
/// <para><b>外した点が制約ノックアウトの生き残りにならないようにする</b>（ADR-0053）。
/// この表は守りを 8 つ持つが、<b>撃つテストが無ければ「どんなテストを書いても殺せない」数に
/// 混ざってしまう</b>——上限は「包まれていて原理的に殺せない数」なので、上げずにテストを足す。</para>
/// </remarks>
public class TransitionRateConstraintTests
{
    private const string Columns =
        "(valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)";

    /// <summary>その期間の行を 1 本入れる SQL。<b>版の字は期間から導く</b>（表の規則）。</summary>
    private static string Insert(
        string from, string to, string percent = "80", string? version = null, string basis = "'検体'")
        => $"INSERT INTO transition_purchase_rates {Columns} VALUES"
            + $" ('{from}', '{to}', {percent}, {version ?? $"'transition_purchase_rate:{from}'"},"
            + $" {basis}, '検体', '2026-09-10');";

    private static SqliteConnection Empty() => TestDatabase.Create();

    /// <summary>
    /// <b>隣り合う期間は通る。</b> 重なりの守りが 1 日ぶん強すぎないこと。
    /// </summary>
    /// <remarks>
    /// <b>断りより先に置く。</b> 締めすぎた守りは、拒む側のテストでは 1 本も赤くならない——
    /// <b>配った 4 行が入らなくなるほうの壊れ方は、ここでしか見えない</b>。
    /// </remarks>
    [Fact]
    public void 隣り合う期間は通る()
    {
        using var db = Empty();

        TestDatabase.Execute(db, Insert("2023-10-01", "2026-09-30"));
        TestDatabase.Execute(db, Insert("2026-10-01", "2028-09-30", "70"));

        Assert.Equal(
            ["2023-10-01|2026-09-30", "2026-10-01|2028-09-30"],
            TestDatabase.Query(
                db,
                "SELECT valid_from || '|' || valid_to FROM transition_purchase_rates ORDER BY valid_from"));
    }

    /// <summary>
    /// <b>1 日でも重なる期間は断る。</b>
    /// </summary>
    /// <remarks>
    /// <b>重なりは一意索引では止まらない</b>（始まりが違えば通る）。
    /// 重なりを許すと「どちらが当たったか」が実行順に依存し、<b>静かに違う割合で計算される</b>。
    /// <b>前にはみ出す形と後ろにはみ出す形の両方を撃つ</b>——片方だけだと、
    /// 条件の不等号を片側だけ壊しても緑のまま通る。
    /// </remarks>
    [Theory]
    [InlineData("2026-09-30", "2028-09-30", "終わりに 1 日重なる")]
    [InlineData("2022-01-01", "2023-10-01", "始まりに 1 日重なる")]
    [InlineData("2024-01-01", "2024-12-31", "丸ごと内側")]
    [InlineData("2020-01-01", "2030-12-31", "丸ごと外側")]
    public void 期間が重なる版は追加できない(string from, string to, string label)
    {
        using var db = Empty();
        TestDatabase.Execute(db, Insert("2023-10-01", "2026-09-30"));

        Rejected.ByTrigger(db, Insert(from, to), "有効期間が既にある版と重なっている。", label);
    }

    /// <summary>更新でも重なりを断る。</summary>
    [Fact]
    public void 期間が重なる版へは更新できない()
    {
        using var db = Empty();
        TestDatabase.Execute(db, Insert("2023-10-01", "2026-09-30"));
        TestDatabase.Execute(db, Insert("2026-10-01", "2028-09-30", "70"));

        // **版の字も一緒に動かす。** 動かさないと版の守りが先に断って、重なりの守りを測れない
        // （トリガの発火順は undefined で、実測では後に作ったものから鳴る）。
        Rejected.ByTrigger(
            db,
            "UPDATE transition_purchase_rates"
            + " SET valid_from = '2026-09-30', version = 'transition_purchase_rate:2026-09-30'"
            + " WHERE version = 'transition_purchase_rate:2026-10-01';",
            "有効期間が既にある版と重なっている。");
    }

    /// <summary>
    /// <b>同じ日から始まる版は 2 つ作れない</b>（時刻が付いていても同じ日である）。
    /// </summary>
    [Fact]
    public void 同じ日から始まる版は2つ作れない()
    {
        using var db = Empty();
        TestDatabase.Execute(db, Insert("2023-10-01", "2026-09-30"));

        // **同じ日から始まれば必ず重なる**ので、重なりのトリガが先に断る——
        // **索引そのものを撃つには、トリガを外すしかない**（許可表を通し、終わったら貼り直す）。
        // **時刻付きで書いても同じ日である**（CLB は日付の列へ "2023-10-01 00:00:00" と書く）。
        var exception = Assert.Throws<SqliteException>(() => TestDatabase.WithoutTrigger(
            db,
            "trg_transition_purchase_rates_no_overlap_insert",
            Insert("2023-10-01 00:00:00", "2026-09-30", "70", "'transition_purchase_rate:2023-10-01'")));

        Assert.Contains(
            "ux_transition_purchase_rates_valid_from", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>終わりが始まりより前の期間は作れない。</summary>
    [Fact]
    public void 終わりが始まりより前の期間は作れない()
    {
        using var db = Empty();

        Rejected.ByCheck(db, Insert("2026-10-01", "2026-09-30"), "date(valid_from) <= date(valid_to)");
    }

    /// <summary>
    /// <b>控除割合は 1 以上 100 以下の整数だけ。</b>
    /// </summary>
    /// <remarks>
    /// <b>0 を許さない</b>——0% の行は「経過措置を 0% で適用する」と読め、
    /// <b>帳簿に旨が立ち、上限の累計にも入る</b>（docs/11 §5-1）。
    /// </remarks>
    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    public void 控除割合は1から100の範囲だけ(string percent)
    {
        using var db = Empty();

        Rejected.ByCheck(db, Insert("2023-10-01", "2026-09-30", percent), "rate_percent BETWEEN 1 AND 100");
    }

    /// <summary>
    /// <b>控除割合に小数は入らない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>CHECK だけでは通ってしまう。</b> INTEGER affinity は<b>非可逆な REAL を変換しない</b>ので、
    /// <c>80.5</c> は REAL のまま残り <c>BETWEEN 1 AND 100</c> を満たす（2026-09-23 実測）。</para>
    /// <para><b>入った瞬間に読めなくなる。</b> 読み出し側（<c>DbValue.ToLong</c>）は小数を見つけたら投げるので、
    /// <b>DB が受けた行を読むたびに計上が止まる</b>——011 が書いた
    /// 「DB が広いと、読んだ瞬間に落ちる行が作れる」形である。</para>
    /// </remarks>
    [Fact]
    public void 控除割合に小数は入らない()
    {
        using var db = Empty();

        Rejected.ByTrigger(
            db,
            Insert("2023-10-01", "2026-09-30", "80.5"),
            "「控除割合」は 1 以上 100 以下の整数で入れる。");
    }

    /// <summary>
    /// <b>自然キーと法源に BLOB は入らない。</b>
    /// </summary>
    /// <remarks>
    /// <b>BLOB は TEXT の列にそのまま入り、UNIQUE でもぶつからない</b>（010 が 2026-09-09 に実測）。
    /// 読み出し側（<c>DbValue.ToText</c>）は byte[] を <c>System.Byte[]</c> にするので、
    /// <b>その字が版として仕訳に焼かれ、版で引き直せなくなる</b>。
    /// </remarks>
    [Theory]
    [InlineData("version")]
    [InlineData("legal_basis")]
    [InlineData("source_url")]
    public void 文字の列にBLOBは入らない(string column)
    {
        using var db = Empty();

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = "'transition_purchase_rate:2023-10-01'",
            ["legal_basis"] = "'検体'",
            ["source_url"] = "'検体'",
        };
        values[column] = "x'61646D696E'";

        Rejected.ByTrigger(
            db,
            $"INSERT INTO transition_purchase_rates {Columns} VALUES"
            + $" ('2023-10-01', '2026-09-30', 80, {values["version"]},"
            + $" {values["legal_basis"]}, {values["source_url"]}, '2026-09-10');",
            "「版」「法源」「出典 URL」は文字で入れる。",
            column);
    }

    /// <summary>
    /// <b>版の字は有効期間の開始日から導く。</b>
    /// </summary>
    /// <remarks>
    /// 形を決めただけでは、<b>版が指す日と、その版が実際に効いた期間が食い違ったまま仕訳に焼ける</b>
    /// （版は <c>journal_lines.applied_rule_version</c> に写す。I-16）。
    /// </remarks>
    [Theory]
    [InlineData("'transition_purchase_rate:2024-01-01'", "日がずれている")]
    [InlineData("'transitional-deduction@2023-10-01'", "種類の字が違う")]
    [InlineData("'2023-10-01'", "種類が無い")]
    public void 版の字が期間と食い違う行は作れない(string version, string label)
    {
        using var db = Empty();

        Rejected.ByTrigger(
            db,
            Insert("2023-10-01", "2026-09-30", "80", version),
            "「版」は「transition_purchase_rate:<有効期間の開始日>」の形で入れる。",
            label);
    }

    /// <summary>
    /// <b>版の断りは、日付が読めるときだけ鳴る。</b>
    /// </summary>
    /// <remarks>
    /// <b>SQLite のトリガの発火順は仕様上 undefined</b> で、実測では<b>後に作ったものから鳴る</b>——
    /// 版のトリガは最後に作られるので、<b>日付が読めない値のときに版の断りが先に鳴ると、
    /// 直すべき日付の断りが出ない</b>（2026-09-23 に実際に出した）。
    /// </remarks>
    [Theory]
    [InlineData("20231001", "区切り無し")]
    [InlineData("2023-13-01", "月が 13")]
    [InlineData("2460000", "ユリウス日")]
    public void 日付が読めないときは日付の断りが出る(string from, string label)
    {
        using var db = Empty();

        Rejected.ByTrigger(
            db,
            Insert(from, "2026-09-30"),
            "「有効期間の開始日」は実在する年月日を「2026-04-01」の形で入れる。",
            label);
    }
}
