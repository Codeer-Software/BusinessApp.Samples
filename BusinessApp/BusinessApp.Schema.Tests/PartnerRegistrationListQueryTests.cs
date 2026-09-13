namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 登録の一覧（docs/14 §4）を<b>本物の DDL に本物の SQL を流して</b>読み戻す。
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は宣言と SQL の整合しか見ない。しかも突き合わせているのは
/// <b>列の別名だけ</b>なので、<c>r.confirmed_on AS ended_on</c> のような<b>中身の取り違えは素通りする</b>。
/// 並び順も、部分一致の逃がし方も、日付の境界も、<b>行を入れて読み戻さないと分からない</b>
/// （qa/03 の L-12・L-19・L-20）。</para>
/// <para><b>この一覧は 2 週間、その検査を持っていなかった</b>——
/// <see cref="QueryBehaviorCoverageTests"/> を入れた日に見つかった（qa/02 のラウンド 91）。</para>
/// </remarks>
[Collection(QuerySqlCollection.Name)]
public class PartnerRegistrationListQueryTests
{
    /// <summary>
    /// 取引先 2 件・登録 4 件。
    /// </summary>
    /// <remarks>
    /// <para><b>識別子の順とコードの順を逆にしてある。</b> 後から入れる取引先（id = 2）の
    /// コードを <c>P000</c> にしたので、<b>コード順では先頭に来る</b>。
    /// <c>ORDER BY p.code</c> を <c>p.id</c> に取り違えると答えが変わる
    /// ——**コード順＝識別子順の検体では、取り違えが永久に見えない**（qa/03 の L-19）。</para>
    /// <para><b>日付の 4 列は、行の中でも行のあいだでも全部違う日にしてある。</b>
    /// <c>valid_from</c>・<c>ended_on</c>・<c>confirmed_on</c>・<c>nta_updated_on</c> が同じ日だと、
    /// <b>列の取り違えが見えない</b>（qa/02 の R89-05）。
    /// <c>source</c> と <c>end_reason</c> は取りうる値が 3 つと 2 つしか無いので重なるが、
    /// <b>行まるごとの組としては全部違う</b>ので取り違えは見える。</para>
    /// <para><b>部分一致の検体には <c>%</c> と <c>_</c> と <c>\</c> を含む登録番号を入れてある</b>——
    /// 逃がし漏れは「打った文字がワイルドカードになる」形で出るので、
    /// <b>その文字を含む行が実在しないと検出できない</b>。</para>
    /// <para><b>同じ取引先の 3 件は期間が重ならない</b>（登録 → 取消 → 再登録 → 失効 → 再登録の履歴）。
    /// 重なると DDL のトリガが断る——<b>開いたままの登録は 1 件だけ</b>である
    /// （<c>trg_partner_invoice_registrations_no_overlap_insert</c>）。</para>
    /// </remarks>
    private const string Registrations = """
        INSERT INTO partners (code, name) VALUES ('P000', 'あとから足した取引先');
        INSERT INTO partner_invoice_registrations
            (partner_id, registration_no, valid_from, ended_on, end_reason, source, published_name, confirmed_on, nta_updated_on)
            VALUES (1, 'T1000000000001', '2026-04-01', '2026-05-14', 'revoked', 'manual', '公表名その一', '2026-04-10', '2026-04-11');
        INSERT INTO partner_invoice_registrations
            (partner_id, registration_no, valid_from, ended_on, end_reason, source, published_name, confirmed_on, nta_updated_on)
            VALUES (1, 'T2000000000002', '2026-05-15', '2026-06-30', 'expired', 'nta_api', '公表名その二', '2026-07-01', '2026-07-02');
        INSERT INTO partner_invoice_registrations
            (partner_id, registration_no, valid_from, ended_on, end_reason, source, published_name, confirmed_on, nta_updated_on)
            VALUES (1, 'T3000000000003', '2026-07-20', NULL, NULL, 'nta_download', NULL, NULL, NULL);
        INSERT INTO partner_invoice_registrations
            (partner_id, registration_no, valid_from, ended_on, end_reason, source, published_name, confirmed_on, nta_updated_on)
            VALUES (2, 'T5\0%_000000004', '2026-04-02', NULL, NULL, 'manual', '公表名その四', '2026-04-20', '2026-04-21');
        """;

    /// <summary>
    /// <b>12 列すべてを、行まるごと突き合わせる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>列の取り違えは、これでしか捕まらない。</b> <c>QueryModuleTests</c> が見るのは
    /// <b>別名だけ</b>なので、<c>r.nta_updated_on AS confirmed_on</c> と書いても宣言とは一致する。
    /// 絞り込みも並びも <c>r.valid_from</c> しか使っていないので、他のテストも全部緑のままになる。</para>
    /// <para><b>とくに <c>reg_id</c> が効く。</b> この列はデザインの編集リンク
    /// （<c>EditLink.IdVariable = RegId.Value</c>）が使う値である。
    /// <b>いまその列は画面に出ていない</b>——この一覧は読み取り専用で、入力の導線は
    /// 取引先の詳細だけにしてある（docs/14 §4。qa/04 の R-04）。
    /// <b>出した日に取り違えていれば、別の登録行が開く。</b>
    /// <b>誰も見ていない列こそ、壊れても誰も気づかない。</b></para>
    /// <para><b>値が全部埋まった行と、任意の列が全部 NULL の行の 2 本を見る。</b>
    /// 埋まった行だけだと、NULL を読む経路が一度も通らない。</para>
    /// </remarks>
    [Fact]
    public void 十二列がそれぞれの列の値を返す()
    {
        using var db = Create();

        var rows = Run(db);

        Assert.Equal(
            new Row(4, 2, "P000", "あとから足した取引先", "T5\\0%_000000004",
                    "2026-04-02", null, null, "公表名その四", "2026-04-20", "manual", "2026-04-21"),
            rows[0]);
        Assert.Equal(
            new Row(2, 1, "P001", "株式会社取引先", "T2000000000002",
                    "2026-05-15", "2026-06-30", "expired", "公表名その二", "2026-07-01", "nta_api", "2026-07-02"),
            rows[2]);
        Assert.Equal(
            new Row(3, 1, "P001", "株式会社取引先", "T3000000000003",
                    "2026-07-20", null, null, null, null, "nta_download", null),
            rows[3]);
    }

    /// <summary>
    /// 並びは<b>取引先コード順 → 登録年月日の古い順</b>である。
    /// </summary>
    /// <remarks>
    /// <b>検体は識別子の順とコードの順が逆</b>なので、<c>ORDER BY p.id</c> に取り違えると
    /// 先頭の取引先が入れ替わる。<b>同じ取引先の中の並びは登録年月日で決まる</b>。
    /// </remarks>
    [Fact]
    public void 取引先コードの順に並び_同じ取引先の中は登録年月日の古い順になる()
    {
        using var db = Create();

        var rows = Run(db);

        Assert.Equal(
            ["T5\\0%_000000004", "T1000000000001", "T2000000000002", "T3000000000003"],
            rows.Select(row => row.RegistrationNo));
    }

    /// <summary>
    /// <b>コードを入れ替えるだけで並びが変わる</b>——並べているのはコードであって識別子ではない。
    /// </summary>
    [Fact]
    public void 取引先のコードを入れ替えると並びも入れ替わる()
    {
        using var db = Create();
        TestDatabase.Execute(db, """
            UPDATE partners SET code = 'P900' WHERE id = 2;
            UPDATE partners SET code = 'P500' WHERE id = 1;
            """);

        var rows = Run(db);

        Assert.Equal(
            ["T1000000000001", "T2000000000002", "T3000000000003", "T5\\0%_000000004"],
            rows.Select(row => row.RegistrationNo));
    }

    /// <summary>
    /// 取引先名は<b>それぞれの登録の取引先</b>の現在名を出す（写しを持たない一覧である）。
    /// </summary>
    /// <remarks>
    /// <b>2 件とも見る。</b> 1 件だけだと、<c>JOIN partners p ON p.id = 1</c> のような
    /// <b>全行に同じ取引先をぶら下げる書き損じ</b>が通る。
    /// </remarks>
    [Fact]
    public void 取引先を改名すると一覧の名前も動く()
    {
        using var db = Create();
        TestDatabase.Execute(db, """
            UPDATE partners SET name = '改名した一番' WHERE id = 1;
            UPDATE partners SET name = '改名した二番' WHERE id = 2;
            """);

        var rows = Run(db);

        Assert.Equal(
            ["改名した二番", "改名した一番", "改名した一番", "改名した一番"],
            rows.Select(row => row.PartnerName));
    }

    /// <summary>
    /// <b>区分値は生のまま返る。</b> 日本語の見出しは CLB のデザイン enum が持つ。
    /// </summary>
    /// <remarks>
    /// SQL で <c>CASE</c> を書くと、C# の列挙型・DDL の <c>CHECK</c>・デザイン enum に続く
    /// <b>4 つ目の写し</b>になる（SQL のヘッダの注記）。<b>写しが増えていないこと</b>を見る。
    /// </remarks>
    [Fact]
    public void 終わりの理由と出所は生の値のまま返る()
    {
        using var db = Create();

        var rows = Run(db);

        Assert.Equal([null, "revoked", "expired", null], rows.Select(row => row.EndReason));
        Assert.Equal(
            ["manual", "manual", "nta_api", "nta_download"],
            rows.Select(row => row.Source));
    }

    /// <summary>検索欄が空のときは絞り込まない。<b>NULL と空文字の両方</b>で。</summary>
    /// <remarks>
    /// CLB は空の検索欄を <b>NULL または空文字</b>で束縛する（_specs/QueryAndSql.md）。
    /// <b>片方しか見ていないと、空欄のまま検索したときに 0 件になる</b>——
    /// 画面は「該当なし」を出すので、静かな失敗になる。
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 検索欄が空なら絞り込まない(string? empty)
    {
        using var db = Create();

        var rows = Run(db, new()
        {
            ["@p_partner_id"] = empty,
            ["@p_registration_no"] = empty,
            ["@p_valid_from_from"] = empty,
            ["@p_valid_from_to"] = empty,
        });

        Assert.Equal(4, rows.Count);
    }

    /// <summary>
    /// 取引先で絞り込める。
    /// </summary>
    /// <remarks>
    /// <b>本番が束縛する型でも通す。</b> デザインの宣言は <c>DbType: "text"</c> で
    /// （`PartnerRegistrationList.mod.json` の `p_partner_id`）、画面からは
    /// <b>選択欄の値が文字列で来る</b>。SQLite の列アフィニティが吸収しているが、
    /// <b>吸収していることをテストが言っていなければ、言えていないのと同じ</b>である。
    /// </remarks>
    [Theory]
    [InlineData(2L)]
    [InlineData("2")]
    public void 取引先で絞り込める(object partnerId)
    {
        using var db = Create();

        var rows = Run(db, new() { ["@p_partner_id"] = partnerId });

        Assert.Equal("T5\\0%_000000004", Assert.Single(rows).RegistrationNo);
    }

    /// <summary>登録番号は部分一致で絞り込める。</summary>
    [Fact]
    public void 登録番号は部分一致で絞り込める()
    {
        using var db = Create();

        var rows = Run(db, new() { ["@p_registration_no"] = "0000000002" });

        Assert.Equal("T2000000000002", Assert.Single(rows).RegistrationNo);
    }

    /// <summary>
    /// <b>利用者が打った <c>%</c> と <c>_</c> をワイルドカードにしない。</b>
    /// </summary>
    /// <remarks>
    /// <para>逃がし漏れがあると <c>%</c> は「何でも」になり、<b>1 件を探したつもりが別の行が出る</b>。
    /// <c>_</c> は「任意の 1 文字」になるので、<b>別の行が混ざる</b>——
    /// どちらも 0 件にはならないぶん、画面を見ても気づきにくい。</para>
    /// <para><b>検体は「逃がしを外すと答えが変わる」形でなければ何も言っていない。</b>
    /// はじめは <c>T1%4</c> を置いていたが、これは<b>逃がしても外しても 0 件</b>で、
    /// **どう壊しても赤くならない**（ADR-0012 §2 が禁じた「通るだけのテスト」。
    /// 2026-09-14 の自己レビューで 2 人が独立に見つけた）。
    /// <b>下の 5 つは、5 通りの壊し方を 1 つずつ殺せることを実測してある</b>——
    /// 逃がしを丸ごとやめる・順序を逆にする・<c>%</c> だけ素通し・<c>_</c> だけ素通し・<c>\</c> だけ素通し。</para>
    /// <para><b>逃がす順序は「まず <c>\</c>、次に <c>%</c> と <c>_</c>」である。</b>
    /// 逆にすると、付けたばかりの <c>\</c> をもう一度逃がして二重になる。
    /// <b>検体の 4 件目に <c>\</c> を入れてあるのは、この順序を撃つため</b>である。</para>
    /// </remarks>
    [Theory]
    // 打った通りの行だけが出る（`%` も `_` も `\` もただの文字として扱われる）。
    // **逃がしをやめても、順序を逆にしても、`\` を素通しにしても 0 件になる**——3 通りを殺す。
    [InlineData("T5\\0%_000000004", 1)]
    // 逃がしをやめると全 4 件になる（`%` と `_` がワイルドカードに化ける）。
    [InlineData("0%_0", 1)]
    // `%` を素通しにしていると `T1000000000001` が当たって 1 件になる。
    [InlineData("T1%1", 0)]
    // `_` を素通しにしていると `T5\0%_000000004` が当たって 1 件になる。
    [InlineData("T5_______000004", 0)]
    // 逃がし文字そのもの。**検体に実在する**ので 1 件が正しい——
    // 逃がしをやめると 0 件になり、`\` だけ素通しにしても 0 件になる。
    [InlineData("\\", 1)]
    public void 登録番号の部分一致は打った文字をワイルドカードにしない(string keyword, int expected)
    {
        using var db = Create();

        var rows = Run(db, new() { ["@p_registration_no"] = keyword });

        Assert.Equal(expected, rows.Count);
    }

    /// <summary>
    /// <b>登録年月日の範囲は両端を含む。</b>
    /// </summary>
    /// <remarks>
    /// <c>&gt;=</c> を <c>&gt;</c> に取り違えると、<b>境界の 1 日だけが落ちる</b>——
    /// 件数が 1 件違うだけなので、目で見ても気づかない。
    /// </remarks>
    [Theory]
    [InlineData("2026-05-15", null, 2)]
    [InlineData(null, "2026-05-15", 3)]
    [InlineData("2026-05-15", "2026-05-15", 1)]
    [InlineData("2026-05-16", null, 1)]
    [InlineData(null, "2026-05-14", 2)]
    public void 登録年月日の範囲は両端を含む(string? from, string? to, int expected)
    {
        using var db = Create();

        var rows = Run(db, new() { ["@p_valid_from_from"] = from, ["@p_valid_from_to"] = to });

        Assert.Equal(expected, rows.Count);
    }

    /// <summary>
    /// <b>日付は列の側も検索値の側も <c>date()</c> を通して比べる。</b> 辞書順のままでは境界が落ちる。
    /// </summary>
    /// <remarks>
    /// <para>DATE 列の正規形は <c>'YYYY-MM-DD 00:00:00'</c>（qa/01 の A-04）だが、
    /// <b>保存された値も検索欄から来る値も、時刻が付いているとは限らない</b>。
    /// <b>4 通りの組み合わせを全部通す</b>——片側にしか <c>date()</c> が無いと、
    /// <b>その組み合わせのときだけ境界の 1 日が落ちる</b>。</para>
    /// <para><b>2 つの向きが要る。</b> 列に時刻が付いていると
    /// <c>'2026-05-15 00:00:00' &lt;= '2026-05-15'</c> が偽になり<b>終わりの境界</b>が落ちる。
    /// 検索値に時刻が付いていると <c>'2026-05-15' &gt;= '2026-05-15 00:00:00'</c> が偽になり
    /// <b>始まりの境界</b>が落ちる。<b>片方だけの検体では、もう片方の <c>date()</c> を外しても緑のまま</b>
    /// だった（2026-09-14 に SQL を 1 箇所ずつ壊して実測。qa/02 のラウンド 91）。</para>
    /// </remarks>
    [Theory]
    [InlineData("2026-05-15", "2026-05-15", "2026-05-15")]
    [InlineData("2026-05-15 00:00:00", "2026-05-15", "2026-05-15")]
    [InlineData("2026-05-15", "2026-05-15 00:00:00", "2026-05-15 00:00:00")]
    [InlineData("2026-05-15 00:00:00", "2026-05-15 00:00:00", "2026-05-15 00:00:00")]
    public void 時刻が付いていても日付として比べられる(string stored, string from, string to)
    {
        using var db = Create();
        TestDatabase.Execute(
            db, $"UPDATE partner_invoice_registrations SET valid_from = '{stored}' WHERE id = 2");

        var rows = Run(db, new() { ["@p_valid_from_from"] = from, ["@p_valid_from_to"] = to });

        Assert.Equal("T2000000000002", Assert.Single(rows).RegistrationNo);
    }

    private static SqliteConnection Create()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, Registrations);
        return db;
    }

    /// <summary>SQL が返す 12 列。<b>1 つも省かない</b>（省いた列は誰も見ていないことになる）。</summary>
    private sealed record Row(
        long RegId, long PartnerId, string PartnerCode, string PartnerName, string RegistrationNo,
        string ValidFrom, string? EndedOn, string? EndReason, string? PublishedName,
        string? ConfirmedOn, string Source, string? NtaUpdatedOn);

    /// <summary>一覧の SQL を<b>本物のまま</b>流す。渡さなかったパラメータは NULL（＝条件なし）。</summary>
    /// <remarks>
    /// <b>知らないパラメータ名を黙って捨てない。</b> 綴りを誤ると
    /// <b>その条件が無いものとして流れ、「絞り込まれないこと」を見るテストが間違った理由で緑になる</b>。
    /// </remarks>
    private static IReadOnlyList<Row> Run(
        SqliteConnection db, Dictionary<string, object?>? arguments = null)
    {
        var unknown = arguments is null
            ? []
            : arguments.Keys.Where(name => !Parameters.Contains(name)).ToList();
        Assert.True(
            unknown.Count == 0,
            $"この SQL に無いパラメータを渡している: {string.Join(" / ", unknown)}");

        using var command = db.CreateCommand();
        command.CommandText = TestDatabase.QuerySql("PartnerRegistrationList");

        foreach (var name in Parameters)
        {
            command.Parameters.AddWithValue(
                name,
                arguments is not null && arguments.TryGetValue(name, out var value) && value is not null
                    ? value
                    : DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt64(reader.GetOrdinal("reg_id")),
                reader.GetInt64(reader.GetOrdinal("partner_id")),
                reader.GetString(reader.GetOrdinal("partner_code")),
                reader.GetString(reader.GetOrdinal("partner_name")),
                reader.GetString(reader.GetOrdinal("registration_no")),
                reader.GetString(reader.GetOrdinal("valid_from")),
                Text(reader, "ended_on"),
                Text(reader, "end_reason"),
                Text(reader, "published_name"),
                Text(reader, "confirmed_on"),
                reader.GetString(reader.GetOrdinal("source")),
                Text(reader, "nta_updated_on")));
        }

        return rows;
    }

    private static string? Text(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>SQL が使う入力パラメータ。<b>足りないと SQLite が実行時に落ちる</b>。</summary>
    private static readonly string[] Parameters =
    [
        "@p_partner_id", "@p_registration_no", "@p_valid_from_from", "@p_valid_from_to",
    ];
}
