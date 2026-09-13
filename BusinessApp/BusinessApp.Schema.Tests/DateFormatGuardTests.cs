namespace BusinessApp.Schema.Tests;

using System.Text.RegularExpressions;

using BusinessApp.ServerSupport;
using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 日付の列は年月日として読める値だけを受け取る（<c>Designer/ddl/011_date_format.sql</c>）。
/// </summary>
/// <remarks>
/// <para><b>1 つの規則が 5 表 12 列に当たる。</b> <see cref="MasterCodeGuardTests"/> と同じ形なので、
/// 既存のクラスに混ぜず 1 本にまとめる——<b>「トリガが全部同じ字であること」を 1 か所で見張れる</b>。</para>
/// <para><b>SQLite の日付は文字列である。</b> 列を <c>DATE</c> と宣言しても affinity は NUMERIC でしかなく、
/// <c>'20260401'</c> も <c>'2026-6-1'</c> も <c>'now'</c> もそのまま入る。入った瞬間に
/// <b>①式索引（<c>date(valid_from)</c>）が NULL 同士を別物として通す ②期間の重なりのトリガが
/// 比較ごと NULL になって 1 本も鳴らない ③文字列のまま比べている CHECK が辞書順で
/// <c>'2026-06-30' &lt;= '2026-6-1'</c> を真にする</b>——の 3 つが同時に効かなくなる。
/// <b>§「なぜ置いたか」の 3 本がその 3 つを名指しで測る</b>。</para>
/// <para><b>断りは <see cref="Rejected.ByTrigger"/> で見る。</b> 素の <c>Assert.Throws</c> だと、
/// NOT NULL・CHECK・UNIQUE が先に鳴っていても緑になり、<b>日付のトリガを一度も撃たないまま通る</b>
/// （qa/03 の L-45）。検体はどれも<b>他の制約に当たらない値</b>で組んである——
/// 種まきと衝突しない年度コード・開始日、<c>CHECK (start_date &lt;= end_date)</c> を越えない基準日、
/// <c>CHECK ((ended_on IS NULL) = (end_reason IS NULL))</c> を満たす取消理由。</para>
/// </remarks>
public class DateFormatGuardTests
{
    /// <summary>日付の列を持つ表（011）。</summary>
    public static TheoryData<string> Tables => new()
    {
        "fiscal_years", "accounting_periods", "journal_entries", "journal_lines", "partner_invoice_registrations",
    };

    // ------------------------------------------------------------------------------------------
    // 通るべき値が通ること
    //
    // **断りより先に置く。** 守りを締めすぎた変更は、拒む側のテストでは 1 本も赤くならない——
    // 画面からの保存が全部落ちるほうの壊れ方は、ここでしか見えない。
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>CLB が日付欄へ書く形は、12 列とも通る。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>CLB は <c>DateOnly</c> を <c>'2023-10-01 00:00:00'</c> と時刻付きで書く</b>
    /// （<c>ux_partner_invoice_registrations_valid_from</c> が <c>date()</c> で包んである理由でもある）。
    /// 011 の条件は<b>先頭 10 文字がその値の指す日そのものであること</b>なので、
    /// <c>date()</c> が読める書き方——ミリ秒・<c>T</c> 区切り・末尾の <c>Z</c>——はどれも通らなければならない。
    /// <b>ここが赤くなるということは、画面からの保存が全部落ちるということである。</b></para>
    /// <para><b>入った値をそのまま読み返す。</b> 通ったことだけを見ると、
    /// NUMERIC affinity が値を整数へ化けさせていても気づけない。</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Accepted))]
    public void CLBが日付欄へ書く形は通る(string table, string column, string value)
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, Insert(table, column, "'" + value + "'"));

        Assert.Equal(
            value,
            TestDatabase.ScalarOf<string>(db, $"SELECT {column} FROM {table} ORDER BY id DESC LIMIT 1"));
    }

    /// <summary>
    /// <b>NULL 可の 2 列は NULL を通す。</b>
    /// </summary>
    /// <remarks>
    /// 011 の条件が <c>&lt;&gt;</c> ではなく <c>IS NOT</c> なのは読めない値を断るためだが、
    /// <b>その副作用として NULL は両辺 NULL になって通る</b>。ここが赤くなると
    /// <b>「優良な電子帳簿の適用開始日」を空にした会計年度と、登録が生きている
    /// （取消・失効年月日の無い）インボイス登録が、どちらも作れなくなる</b>。
    /// </remarks>
    [Theory]
    [InlineData(
        "INSERT INTO fiscal_years (code, label, start_date, end_date, premium_ledger_from)"
        + " VALUES ('FY99', '検体', '2028-04-01', '2029-03-31', NULL);",
        "fiscal_years",
        "premium_ledger_from")]
    [InlineData(
        "INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from, ended_on, end_reason)"
        + " VALUES (1, 'T1234567890123', '2023-10-01', NULL, NULL);",
        "partner_invoice_registrations",
        "ended_on")]
    public void NULL可の列はNULLを通す(string sql, string table, string column)
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, sql);

        // **表と列も渡して読み返す。** 「例外が出なかった」だけだと、
        // 値が NULL のまま入ったのか別の何かに化けたのかを言えない。
        Assert.Equal(
            1L,
            TestDatabase.ScalarOf<long>(db, $"SELECT COUNT(*) FROM {table} WHERE {column} IS NULL"));
    }

    /// <summary>
    /// <b>実在する閏日は通る。</b>
    /// </summary>
    /// <remarks>
    /// 011 の条件は<b>先頭 10 文字と <c>date()</c> の突き合わせ</b>なので、
    /// <b>2 月 29 日という形そのものを疑う向きに壊すと、4 年に 1 度の取引を 1 日ぶん入力できなくなる</b>。
    /// 閏日は <c>date()</c> がそのまま返すので通らなければならない。
    /// </remarks>
    [Fact]
    public void 閏年の2月29日は通る()
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, Insert("journal_entries", "transaction_date", "'2028-02-29'"));

        Assert.Equal(
            "2028-02-29",
            TestDatabase.ScalarOf<string>(db, "SELECT transaction_date FROM journal_entries"));
    }

    /// <summary>
    /// <b>初期データ（<c>Designer/seed/</c>）はこの守りを通る。</b>
    /// </summary>
    /// <remarks>
    /// <c>SeedDataTests</c> も seed を流すので重複ではあるが、<b>011 を足した回に落ちる先を名指しで持つ</b>——
    /// 会計年度と会計期間の日付は seed が書いており、<b>1 つでも ISO でなければ DB を立ち上げられない</b>。
    /// </remarks>
    [Fact]
    public void 初期データはこの守りを通る()
    {
        using var db = TestDatabase.CreateWithSeed();
        var checked_ = 0L;

        foreach (var (table, column, _, _) in Columns)
        {
            // **「流せた」だけでは足りない。** 入っている値が本当に ISO かを 1 列ずつ数える——
            // トリガを外した日に、種まきの値が崩れても誰も気づかなくなる。
            Assert.Equal(
                0L,
                TestDatabase.ScalarOf<long>(db, $"""
                    SELECT COUNT(*) FROM {table}
                     WHERE {column} IS NOT NULL
                       AND date(julianday({column})) IS NOT substr({column}, 1, 10);
                    """));
            checked_ += TestDatabase.ScalarOf<long>(
                db, $"SELECT COUNT(*) FROM {table} WHERE {column} IS NOT NULL");
        }

        // **0 件を数えて 0 件だった、では緑ではない。** 実際に値を見た件数を要求する。
        Assert.True(checked_ > 0, "初期データに日付の値が 1 つも無い。検体が空回りしている。");
    }

    /// <summary>
    /// <b>正しい値なら、下書きの取引日は更新できる。</b>
    /// </summary>
    /// <remarks>
    /// <b>拒むほうだけを測ると、常に拒むトリガでも緑になる。</b>
    /// <c>BEFORE UPDATE OF transaction_date, posting_date</c> が
    /// 「読める値まで断つ」形に壊れたら、ここだけが赤くなる。
    /// </remarks>
    [Fact]
    public void 下書きの取引日は正しい値なら更新できる()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);

        TestDatabase.Execute(db, Update("journal_entries", "transaction_date", "'2026-05-21'"));

        Assert.Equal(
            "2026-05-21",
            TestDatabase.ScalarOf<string>(db, "SELECT transaction_date FROM journal_entries"));
    }

    // ------------------------------------------------------------------------------------------
    // トリガが在ること
    // ------------------------------------------------------------------------------------------

    /// <summary>日付の列を持つ表すべてに、追加と更新のトリガが 1 本ずつある。</summary>
    /// <remarks>
    /// <b>011 は「対象の表の DDL ファイルの末尾に足す」を外して 1 ファイルにまとめてある</b>
    /// （既存 DB への配達は常に末尾へ作るので、002 の末尾へ足すと正典と作成順が食い違う）。
    /// <b>1 ファイルにまとめた以上、書き落としはそのファイルの中でしか見えない。</b>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Tables))]
    public void 日付の列を持つ表すべてに追加と更新のトリガがある(string table)
    {
        using var db = SchemaSeed.Create();

        Assert.Equal(
            [$"trg_{table}_date_format_insert", $"trg_{table}_date_format_update"],
            TestDatabase.Query(
                db,
                "SELECT name FROM sqlite_master WHERE type = 'trigger'"
                + $" AND tbl_name = '{table}' AND name LIKE '%date_format%' ORDER BY name"));
    }

    /// <summary>
    /// <b>9 つの列が 1 つ残らず見張られている。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>列を 1 つ書き落としても、他のテストは緑のままである</b>——
    /// トリガは在るし、残りの列は断られるし、通るべき値も通る。
    /// <b>稼働しているトリガの本文から <c>date(NEW.…)</c> の列名を拾い</b>、
    /// 011 が当てる列と突き合わせる（上の「トリガが在ること」を別建てで置くのと同じ理由）。</para>
    /// <para><b>期待値は書き出さない。<c>pragma_table_info</c> から数える。</b>
    /// 書き出していたときに 3 列（<c>tax_point</c>・<c>confirmed_on</c>・<c>nta_updated_on</c>）を数え落とし、
    /// <b>守ったつもりの列が守られていなかった</b>（2026-09-14。qa/02 のラウンド 89）。</para>
    /// </remarks>
    [Fact]
    public void DATEで宣言した列は1つ残らず見張られている()
    {
        using var db = SchemaSeed.Create();
        var groups = Columns.GroupBy(column => column.Table, StringComparer.Ordinal).ToList();

        // **数を直書きしない。スキーマの側から数える。**
        // 直書きしていたときに 3 列（tax_point・confirmed_on・nta_updated_on）を数え落とし、
        // **守ったつもりの列が守られていなかった**（2026-09-14。qa/02 のラウンド 89）。
        var declared = TestDatabase
            .Query(db, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'")
            .SelectMany(table => TestDatabase
                .Query(db, $"SELECT '{table}.' || name FROM pragma_table_info('{table}') WHERE UPPER(type) = 'DATE'"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Equal(
            declared,
            Columns.Select(column => $"{column.Table}.{column.Column}").OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            Tables.Select(row => (string)row[0]).OrderBy(name => name, StringComparer.Ordinal),
            groups.Select(group => group.Key).OrderBy(name => name, StringComparer.Ordinal));

        foreach (var group in groups)
        {
            foreach (var kind in new[] { "insert", "update" })
            {
                var watched = Regex.Matches(Definition(db, group.Key, kind), @"date\(julianday\(NEW\.(\w+)\)\)")
                    .Select(match => match.Groups[1].Value)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal);

                Assert.Equal(group.Select(column => column.Column).OrderBy(name => name, StringComparer.Ordinal), watched);
            }
        }
    }

    /// <summary>
    /// <b>24 か所の条件が、列名を伏せれば 1 字も違わず同じである。</b>
    /// </summary>
    /// <remarks>
    /// <para>12 列 × 追加/更新 で 24 か所。<b>1 か所で <c>IS NOT</c> を <c>&lt;&gt;</c> に書き間違えると、
    /// その列だけが黙って通す</b>——<c>date()</c> が NULL を返す値では比較ごと NULL になり、
    /// <c>WHERE</c> が偽になるからである。<b>いちばん静かな壊れ方</b>で、
    /// 断りのテストを 1 列ぶん書き忘れていたら誰も気づけない。</para>
    /// <para><b>期待する字を書き写さない。</b> 24 か所が互いに同じであることだけを言う——
    /// 正典（011）を直したときに、テストの中の写しだけが古くなることを避ける
    /// （<see cref="MasterCodeGuardTests.トリガ12本は同じ条件を持つ"/> と同じ作り）。</para>
    /// </remarks>
    [Fact]
    public void 条件はどこも同じ字である()
    {
        using var db = SchemaSeed.Create();
        var conditions = new List<string>();

        foreach (var table in Tables.Select(row => (string)row[0]))
        {
            foreach (var kind in new[] { "insert", "update" })
            {
                var body = Regex.Match(Definition(db, table, kind), @"BEGIN\s+(.+)\s*END", RegexOptions.Singleline);
                Assert.True(body.Success, $"trg_{table}_date_format_{kind} の本文を読めない。定義の書き方を変えたら、ここも直す。");

                // 列名を C に伏せ、折り返しの空白も均す（011 は条件を 2 行に分けて書いてある）。
                conditions.AddRange(Regex.Matches(body.Groups[1].Value, @"WHERE\s+(.+?);", RegexOptions.Singleline)
                    .Select(match => Regex.Replace(match.Groups[1].Value, @"NEW\.\w+", "NEW.C"))
                    .Select(condition => Regex.Replace(condition, @"\s+", " ")));
            }
        }

        Assert.Equal(Columns.Length * 2, conditions.Count);
        Assert.All(conditions, condition => Assert.Equal(conditions[0], condition));
    }

    // ------------------------------------------------------------------------------------------
    // 断るべき値が断られること
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>年月日として読めない値は、12 列とも断られる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>文言を全文で渡す。</b> <c>011</c> は 1 本のトリガの中に列ごとの <c>RAISE</c> を並べてあるので、
    /// 「年月日」だけの部分一致だと<b>同じ表の別の列のトリガが鳴っていても緑になる</b>
    /// （<c>fiscal_years</c> は 1 本の中に 3 つ、<c>partner_invoice_registrations</c> は 2 つ持つ）。</para>
    /// <para><b>検体はどれも 011 が名指しした壊れ方である。</b>
    /// <c>'20260401'</c> は<b>式索引を NULL で素通りさせる本命</b>、
    /// <c>'2026-6-1'</c> は<b>辞書順で <c>'2026-06-30' &lt;= '2026-6-1'</c> を真にする</b>形、
    /// <c>'2026-13-01'</c>・<c>'2026-04-32'</c>・<c>'now'</c>・<c>'10:00'</c>・ユリウス日は
    /// <b><c>date()</c> が NULL を返すか、読めてしまうのに先頭 10 文字と食い違う</b>値、
    /// <c>2460000</c> と <c>20260401</c> は<b>NUMERIC affinity で整数のまま居座る</b>形である。
    /// <b>月ごとの日数を越えた日（2 月 30 日など）はここに居ない</b>——
    /// <see cref="実在しない日はいまのSQLiteでは素通りする"/> がその穴を名指しで持つ。</para>
    /// <para><b>最後の 2 つは条件の 2 本目でしか止まらない。</b>
    /// 途中の <c>U+0000</c> は <c>date()</c> も <c>substr()</c> も NUL で止まるので
    /// <b>先頭 10 文字の比較だけならすり抜け、NUL 以降を抱えたまま <c>DATE</c> の列に居座る</b>
    /// （素の文字列比較は NUL 以降も読むので、同じ日が <c>'2026-04-01'</c> より大きいものとして並ぶ）。
    /// BLOB は <c>date()</c> が中身を読んでしまう——010 が自然キーへ当てたのと同じ穴である。
    /// <b>文字列リテラルに NUL を直接書かず SQL 側で連結する</b>のは、途中で切れた SQL を送らないため。</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Unreadable))]
    public void 日付として読めない値は断られる(string table, string column, string label, string value, string message)
    {
        using var db = SchemaSeed.Create();
        var before = TestDatabase.ScalarOf<long>(db, $"SELECT COUNT(*) FROM {table}");

        Rejected.ByTrigger(db, Insert(table, column, value), message, label);

        // **断ったあと、行が残っていないことまで見る。** 例外が飛んだかどうかだけだと、
        // RAISE の種類を ABORT から変えた日に**何のテストも赤くならない**
        // （`MasterCodeGuardTests` は同じ理由で必ず読み戻している）。
        Assert.Equal(before, TestDatabase.ScalarOf<long>(db, $"SELECT COUNT(*) FROM {table}"));
    }

    /// <summary>
    /// <b>更新でも 12 列とも断られる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b><c>BEFORE UPDATE OF &lt;列&gt;</c> を別建てで測る。</b> 追加だけ直して更新を書き落とす形は
    /// <see cref="MasterCodeGuardTests"/> 以前に実在した——<b>列の並びを 1 つ書き落としても、
    /// 追加の側は緑のままである</b>。</para>
    /// <para><b><c>journal_entries</c> は下書きで撃つ。</b> 計上済みで撃つと
    /// <c>trg_journal_entries_posted_no_update</c>（I-05）と日付のトリガの<b>発火順が SQLite の仕様上
    /// undefined</b> で、どちらの <c>RAISE</c> が返るか決まらない（実測では後に作ったほうから鳴るが、頼らない）。</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(UnreadableOnUpdate))]
    public void 更新でも断られる(string table, string column, string message)
    {
        using var db = SchemaSeed.Create();
        var existing = ExistingRowFor(table);
        if (existing.Length > 0)
        {
            TestDatabase.Execute(db, existing);
        }

        var before = TestDatabase.Query(db, $"SELECT COALESCE({column}, '（空）') FROM {table}");

        Rejected.ByTrigger(db, Update(table, column, "'20260401'"), message);

        // **元の値が残っていること。** 断りが効いていても、更新が部分的に通っていたら帳簿は狂う。
        Assert.Equal(before, TestDatabase.Query(db, $"SELECT COALESCE({column}, '（空）') FROM {table}"));
    }

    /// <summary>
    /// <b>月ごとの日数を越えた日は断られる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>この 3 つは、条件を <c>date()</c> だけで書くと素通りしていた。</b>
    /// <c>YYYY-MM-DD</c> と読めた時点で年月日をそのまま持つ版があり、
    /// <c>date()</c> が書いたとおりに返して先頭 10 文字と一致してしまう——
    /// <b>このアプリが載っている SQLite 3.41.2 がそれである</b>（2026-09-14 実測。
    /// Microsoft.Data.Sqlite 8.0.8 ＝ SQLitePCLRaw 2.1.6。3.53.1 では正規化される）。</para>
    /// <para><b>だから条件は <c>date(julianday(x))</c> で包んである</b>——
    /// ユリウス日へ落として戻せば、どちらの版でも 2 月 30 日は 3 月 2 日になる。
    /// <b>このテストは、その版差に依らないことを固定する</b>——
    /// 包みを外した日にも、版が上がった日にも、ここが動く。</para>
    /// <para>月が 13・日が 32 は <c>julianday()</c> が NULL を返すので、別の検体（読めない値）で断られる。
    /// <b>ここは「読めるが実在しない日」だけを撃つ。</b>
    /// 通ると<b>取引年月日（法定記載事項②）が存在しない日を指す</b>。</para>
    /// </remarks>
    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("2026-02-29")]
    [InlineData("2026-04-31")]
    // 閏年の規則の境界。**100 で割れる年は閏年でない**（2100）。
    [InlineData("2100-02-29")]
    public void 読めるが実在しない日は断られる(string value)
    {
        using var db = SchemaSeed.Create();

        Rejected.ByTrigger(
            db,
            Insert("journal_entries", "transaction_date", "'" + value + "'"),
            Message("取引日"),
            value);
    }

    /// <summary><b>400 で割れる年の 2 月 29 日は実在する</b>（閏年の規則のもう一方の境界）。</summary>
    [Fact]
    public void 四百で割れる年の2月29日は通る()
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, Insert("journal_entries", "transaction_date", "'2000-02-29'"));

        Assert.Equal(
            "2000-02-29",
            TestDatabase.ScalarOf<string>(
                db, "SELECT transaction_date FROM journal_entries ORDER BY id DESC LIMIT 1"));
    }

    // ------------------------------------------------------------------------------------------
    // なぜ置いたか（守りが効いた先）
    //
    // **この 3 本が無いと、トリガが在ることは測れても、置いた目的が測れない。**
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// <b>同じ日から始まる登録は、書き方が違っても 2 件入らない。</b>
    /// </summary>
    /// <remarks>
    /// <c>ux_partner_invoice_registrations_valid_from</c> は <c>(partner_id, date(valid_from))</c> で、
    /// <b>読める値どうしなら書き方が違っても同じ日と見る</b>。
    /// <b>011 が無いと <c>'20231001'</c> を 2 件入れられた</b>——<c>date()</c> が NULL を返し、
    /// <b>一意索引は NULL 同士を別物として扱う</b>からである（011 の壊れ方①）。
    /// <b>表の <c>UNIQUE (partner_id, registration_no, valid_from)</c> は生の列なので、
    /// 書き方が違えば当たらない</b>——ここで鳴るのは式索引のほうだけである。
    /// </remarks>
    [Fact]
    public void 同じ日から始まる登録は書き方が違っても2件入らない()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, ExistingRowFor("partner_invoice_registrations"));

        Rejected.ByUnique(
            db,
            "INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)"
            + " VALUES (1, 'T1234567890123', '2023-10-01 00:00:00');",
            "index 'ux_partner_invoice_registrations_valid_from'");
    }

    /// <summary>
    /// <b>読めない登録年月日は、期間の重なりの判定より先に断られる。</b>
    /// </summary>
    /// <remarks>
    /// <c>trg_partner_invoice_registrations_no_overlap_insert</c> は <c>date()</c> で包んだ比較しか持たないので、
    /// <b>読めない値では比較が全部 NULL になり、1 本も鳴らない</b>（011 の壊れ方②）。
    /// <b>つまり重なりのトリガは、この検体に対しては最初から無いのと同じである</b>——
    /// 日付のトリガが断っていることを、文言で名指しして確かめる。
    /// </remarks>
    [Fact]
    public void 読めない登録年月日は期間の重なりの判定より先に断られる()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, ExistingRowFor("partner_invoice_registrations"));

        Rejected.ByTrigger(
            db,
            "INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)"
            + " VALUES (1, 'T9999999999999', '20240401');",
            Message("登録年月日"));
    }

    /// <summary>
    /// <b>0 埋めの無い終了日は、逆転した会計期間を作る前に日付の守りが断つ。</b>
    /// </summary>
    /// <remarks>
    /// <c>CHECK (start_date &lt;= end_date)</c> は文字列のまま比べるので、
    /// <b><c>'2026-06-30' &lt;= '2026-6-1'</c> が辞書順で真になり、終わりが始まりより前の期間が通っていた</b>
    /// （011 の壊れ方③）。<b>ここで赤くなるのは日付のトリガであって CHECK ではない</b>——
    /// 入口で断つので、CHECK まで届かない。
    /// </remarks>
    [Fact]
    public void 会計期間の終わりは始まりより前にできない_0埋めの無い終了日は日付の守りが断つ()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByTrigger(
            db,
            "INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date)"
            + " VALUES (1, '2026-06-30', '2026-6-1');",
            Message("終了日"));
    }

    /// <summary>
    /// <b>ISO に揃えば、逆転した会計期間は既存の CHECK が断つ。</b>
    /// </summary>
    /// <remarks>
    /// <b>日付の守りが効いている状態でこそ、辞書順と暦順が一致する</b>——
    /// <c>CHECK (start_date &lt;= end_date)</c> が「終わりは始まりより前にできない」という
    /// <b>本当の意味で効くのは、ISO で揃った値だけを入れさせているからである</b>。
    /// <b>この 1 本が無いと、011 が守っている当のもの（③）を誰も測っていないことになる。</b>
    /// </remarks>
    [Fact]
    public void 会計期間の終わりは始まりより前にできない_ISOで揃えばCHECKが断つ()
    {
        using var db = SchemaSeed.Create();

        Rejected.ByCheck(
            db,
            "INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date)"
            + " VALUES (1, '2026-06-30', '2026-06-01');",
            "start_date <= end_date");
    }

    /// <summary>
    /// <b>トリガが通す形と、読み出し側が読める形が過不足なく一致する。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>DB の守りが読み出し側より広いと、読んだ瞬間に落ちる行が作れる。</b>
    /// <c>DbValue.ParseDateTime</c> は <c>TryParseExact</c> なので、
    /// <c>'…Z'</c>・<c>'…+09:00'</c>・秒の無い形は読めずに例外を投げる——
    /// <b>その 1 行が入ると、計上のたびに走る <c>AccountingMasterLoader</c> が落ちて計上が全面停止する</b>
    /// （2026-09-14 の自己レビューで指摘。qa/02 のラウンド 89）。</para>
    /// <para><b>逆に狭すぎても困る</b>——画面からの保存が落ちる。だから<b>両方向</b>を見る。
    /// <c>MasterCodeGuardTests.トリガが通す字は関門が通す字と過不足なく一致する</c> と同じ形である。</para>
    /// </remarks>
    [Theory]
    [InlineData("2026-04-01")]
    [InlineData("2026-04-01 00:00:00")]
    [InlineData("2026-04-01 09:30:00.1234567")]
    [InlineData("2026-04-01T09:30:00")]
    [InlineData("2026-04-01T00:00:00Z")]
    [InlineData("2026-04-01T12:00:00+09:00")]
    [InlineData("2026-04-01 09:30")]
    [InlineData("2026-04-01 09:30:00.12345678")]
    [InlineData("2026-04-01 ")]
    [InlineData("20260401")]
    [InlineData("2026-02-30")]
    public void トリガが通す形と読み出し側が読める形は一致する(string value)
    {
        using var db = SchemaSeed.Create();

        var storedByTrigger = true;
        try
        {
            TestDatabase.Execute(db, Insert("journal_entries", "transaction_date", $"'{value}'"));
        }
        catch (SqliteException)
        {
            storedByTrigger = false;
        }

        var readable = true;
        try
        {
            DbValue.ToDate(value);
        }
        catch (InvalidOperationException)
        {
            readable = false;
        }

        Assert.True(
            storedByTrigger == readable,
            $"「{value}」を トリガは{(storedByTrigger ? "通し" : "断り")}、"
            + $"読み出し側は{(readable ? "読める" : "読めない")}。**両側は一致していなければならない。**");
    }

    // ------------------------------------------------------------------------------------------
    // 検体の組み立て
    // ------------------------------------------------------------------------------------------

    /// <summary>12 列 × 読み出し側が読める 5 通りの書き方。</summary>
    public static TheoryData<string, string, string> Accepted
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (table, column, _, baseDate) in Columns)
            {
                data.Add(table, column, baseDate);                    // ISO そのまま
                data.Add(table, column, baseDate + " 00:00:00");      // CLB が日付欄へ書く形
                data.Add(table, column, baseDate + " 00:00:00.000");  // ミリ秒つき
                data.Add(table, column, baseDate + "T09:30:00");      // T 区切り
                data.Add(table, column, baseDate + "T09:30:00.1234567"); // T 区切り＋秒未満 7 桁
            }

            return data;
        }
    }

    /// <summary>12 列 × 断られるべき検体。</summary>
    public static TheoryData<string, string, string, string, string> Unreadable
    {
        get
        {
            var data = new TheoryData<string, string, string, string, string>();
            foreach (var (table, column, label, baseDate) in Columns)
            {
                foreach (var (name, value) in SpecimensOf(baseDate))
                {
                    data.Add(table, column, name, value, Message(label));
                }
            }

            return data;
        }
    }

    /// <summary>12 列（更新の側）。</summary>
    public static TheoryData<string, string, string> UnreadableOnUpdate
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            foreach (var (table, column, label, _) in Columns)
            {
                data.Add(table, column, Message(label));
            }

            return data;
        }
    }

    /// <summary>
    /// 011 が当てている日付の列と、その<b>基準日</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>基準日は「他の制約に当たらない値」として選んである。</b>
    /// <c>fiscal_years</c> は <c>CHECK (start_date &lt;= end_date)</c> を越えない 2028〜2029 年度、
    /// <c>accounting_periods</c> は種まきの 2026-05-01 と <c>UNIQUE (fiscal_year_id, start_date)</c> で
    /// ぶつからない 6 月、<c>partner_invoice_registrations</c> は
    /// <c>CHECK (ended_on &gt;= valid_from)</c> を満たす組にしてある。</para>
    /// <para><b>ここを外すと、拒む側のテストが「トリガが断った」を一度も測らないまま緑になる。</b>
    /// <see cref="Rejected.ByTrigger"/> が拡張エラーコードで種別を守るので<b>赤で気づける</b>が、
    /// 気づけるだけで、検体としては無意味になる。</para>
    /// </remarks>
    private static readonly (string Table, string Column, string Label, string BaseDate)[] Columns =
    [
        ("fiscal_years", "start_date", "開始日", "2028-04-01"),
        ("fiscal_years", "end_date", "終了日", "2029-03-31"),
        ("fiscal_years", "premium_ledger_from", "優良な電子帳簿の適用開始日", "2028-07-01"),
        ("accounting_periods", "start_date", "開始日", "2026-06-01"),
        ("accounting_periods", "end_date", "終了日", "2026-06-30"),
        ("journal_entries", "transaction_date", "取引日", "2026-05-20"),
        ("journal_entries", "posting_date", "計上日", "2026-05-25"),
        ("partner_invoice_registrations", "valid_from", "登録年月日", "2023-10-01"),
        ("partner_invoice_registrations", "ended_on", "取消・失効年月日", "2026-04-01"),
        ("partner_invoice_registrations", "confirmed_on", "最終確認日", "2026-05-11"),
        ("partner_invoice_registrations", "nta_updated_on", "公表システムの更新年月日", "2026-06-22"),
        ("journal_lines", "tax_point", "課税仕入れの時点", "2026-05-20"),
    ];

    /// <summary>断られるべき値（呼び名と、SQL に置く式）。</summary>
    private static IEnumerable<(string Name, string Value)> SpecimensOf(string baseDate) =>
    [
        ("区切り無し", "'20260401'"),
        ("0 埋め無し", "'2026-6-1'"),
        ("スラッシュ", "'2026/04/01'"),
        ("空文字", "''"),
        ("空白だけ", "' '"),
        // **先頭と末尾を分ける。** SQLite の日付の読み取りは 10 文字を読んだあと
        // 空白・タブ・改行・T を読み飛ばすので、**末尾だけの空白は 1 本目の条件を素通りする**
        // （2026-09-14 実測。qa/02 のラウンド 89）。断っているのは形を見る 2 本目である。
        ("先頭に空白", $"' {baseDate}'"),
        ("末尾に空白", $"'{baseDate} '"),
        ("末尾にタブ", $"'{baseDate}' || char(9)"),
        ("末尾に改行", $"'{baseDate}' || char(10)"),
        ("裸の T", $"'{baseDate}T'"),
        // 大文字小文字。SQLite は区切りの t も末尾の z も受けないが、**それを誰も確かめていなかった**。
        ("小文字の t", $"'{baseDate}t09:30'"),
        // 長さ・境界。
        ("年が 5 桁", "'10000-01-01'"),
        ("先頭に負号", $"'-{baseDate}'"),
        ("ユリウス日（実数）", "2460000.5"),
        // 見えない文字。
        ("全角空白", $"'{baseDate}' || char(12288)"),
        ("ゼロ幅", $"'{baseDate}' || char(8203)"),
        ("BOM", $"char(65279) || '{baseDate}'"),
        ("月が 13", "'2026-13-01'"),
        ("日が 32", "'2026-04-32'"),
        ("now", "'now'"),
        ("時刻だけ", "'10:00'"),
        ("ユリウス日（文字列）", "'2460000'"),
        ("ユリウス日（整数）", "2460000"),
        ("整数 8 桁", "20260401"),
        ("全角", "'２０２６－０４－０１'"),
        ("末尾にごみ", $"'{baseDate}x'"),
        // **読み出し側（DbValue）が読めない形。** ここを通すと、入った行を読んだ瞬間に
        // InvalidOperationException が飛び、**計上のたびに走る AccountingMasterLoader が落ちる**。
        ("末尾に Z", $"'{baseDate}T00:00:00Z'"),
        ("時差つき", $"'{baseDate}T12:00:00+09:00'"),
        ("秒が無い", $"'{baseDate} 09:30'"),
        ("秒未満が 8 桁", $"'{baseDate} 09:30:00.12345678'"),
        ("秒未満に数字でない字", $"'{baseDate} 09:30:00.12a'"),
        ("時刻の後ろに CR", $"'{baseDate} 09:30:00' || char(13)"),
        ("途中に U+0000", $"'{baseDate}' || char(0) || 'zzz'"),
        ("BLOB", $"CAST('{baseDate}' AS BLOB)"),
    ];

    /// <summary>断りの文言（011 の <c>RAISE</c>）。欄の呼び名は画面のラベル（docs/21 §2-6）。</summary>
    private static string Message(string label) => $"「{label}」は実在する年月日を「2026-04-01」の形で入れる。";

    /// <summary>
    /// その表の日付の列を基準日で埋め、<paramref name="column"/> だけを <paramref name="value"/> にした追加。
    /// </summary>
    /// <remarks>
    /// <b>撃つ列以外はすべて正しい値にする。</b> 011 は 1 本のトリガに列ごとの <c>RAISE</c> を並べて
    /// 上から評価するので、<b>手前の列も壊れていると、そちらの文言が返って撃ったつもりの列を測れない</b>。
    /// </remarks>
    private static string Insert(string table, string column, string value)
    {
        var values = Columns
            .Where(candidate => candidate.Table == table)
            .ToDictionary(
                candidate => candidate.Column,
                candidate => candidate.Column == column ? value : "'" + candidate.BaseDate + "'",
                StringComparer.Ordinal);

        return table switch
        {
            // 年度コードは種まきの FY18 と別にする（UNIQUE と 008 の書式のトリガに当てない）。
            "fiscal_years" =>
                "INSERT INTO fiscal_years (code, label, start_date, end_date, premium_ledger_from)"
                + $" VALUES ('FY99', '検体', {values["start_date"]}, {values["end_date"]}, {values["premium_ledger_from"]});",
            "accounting_periods" =>
                "INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date)"
                + $" VALUES (1, {values["start_date"]}, {values["end_date"]});",
            // entered_at は NOT NULL。日付ではなく日時の列なので、011 は当てていない。
            "journal_entries" =>
                "INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, entered_at)"
                + $" VALUES (1, {values["transaction_date"]}, {values["posting_date"]}, '2026-05-20 10:00:00');",
            // 登録年月日を撃つ回は取消・失効年月日を書かない——NULL なら
            // CHECK ((ended_on IS NULL) = (end_reason IS NULL)) を満たすので、取消理由も要らない。
            "partner_invoice_registrations" when column == "valid_from" =>
                "INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)"
                + $" VALUES (1, 'T1234567890123', {values["valid_from"]});",
            "partner_invoice_registrations" when column == "ended_on" =>
                "INSERT INTO partner_invoice_registrations"
                + " (partner_id, registration_no, valid_from, ended_on, end_reason)"
                + $" VALUES (1, 'T1234567890123', {values["valid_from"]}, {values["ended_on"]}, 'revoked');",
            // 最終確認日・公表システムの更新年月日。**撃つ列だけを足す**——
            // 取消・失効年月日を書くと end_reason の CHECK も巻き込む。
            "partner_invoice_registrations" =>
                $"INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from, {column})"
                + $" VALUES (1, 'T1234567890123', '2023-10-01', {values[column]});",
            // 明細は伝票にぶら下がるので、下書きを 1 本立ててから入れる。
            "journal_lines" =>
                "INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, entered_at)"
                + " VALUES (1, '2026-05-20', '2026-05-20', '2026-05-20 10:00:00');"
                + " INSERT INTO journal_lines"
                + " (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, tax_point)"
                + $" SELECT MAX(id), 1, 'debit', 1, 100, 1, {values["tax_point"]} FROM journal_entries;",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "日付の列を見張っていない表"),
        };
    }

    /// <summary><paramref name="column"/> を <paramref name="value"/> に変える更新。</summary>
    private static string Update(string table, string column, string value) => table switch
    {
        "fiscal_years" => $"UPDATE fiscal_years SET {column} = {value} WHERE code = 'FY18';",
        "accounting_periods" => $"UPDATE accounting_periods SET {column} = {value} WHERE start_date = '2026-05-01';",
        "journal_entries" =>
            $"UPDATE journal_entries SET {column} = {value}"
            + " WHERE id = (SELECT MAX(id) FROM journal_entries);",
        // 取消・失効年月日を入れる回は理由を添える（CHECK ((ended_on IS NULL) = (end_reason IS NULL))）。
        "partner_invoice_registrations" when column == "ended_on" =>
            $"UPDATE partner_invoice_registrations SET ended_on = {value}, end_reason = 'revoked'"
            + " WHERE registration_no = 'T1234567890123';",
        "partner_invoice_registrations" =>
            $"UPDATE partner_invoice_registrations SET {column} = {value}"
            + " WHERE registration_no = 'T1234567890123';",
        // 検体は ExistingRowFor が先に作る（下書き 1 本と、その 3 行目）。
        "journal_lines" => $"UPDATE journal_lines SET {column} = {value} WHERE line_no = 3;",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "日付の列を見張っていない表"),
    };

    /// <summary>
    /// 更新で撃つための行。<b>種まきに無い表だけが値を返す。</b>
    /// </summary>
    /// <remarks>
    /// <c>journal_entries</c> は<b>下書きまで</b>（計上済みだと I-05 のトリガと発火順を争う）。
    /// <c>partner_invoice_registrations</c> は<b>登録が生きている 1 件</b>——
    /// 1 件しか無ければ <c>trg_partner_invoice_registrations_no_overlap_update</c> は
    /// 自分を除いて数えるので鳴らない。
    /// </remarks>
    private static string ExistingRowFor(string table) => table switch
    {
        "journal_entries" => SchemaSeed.Draft,
        // **検体づくりを更新の SQL に混ぜない。** 混ぜると、断られたあとに
        // 「行が増えていないこと」を見る表明が、検体づくりの行まで数えてしまう。
        "journal_lines" =>
            SchemaSeed.Draft
            + " INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)"
            + " SELECT MAX(id), 3, 'debit', 1, 100, 1 FROM journal_entries;",
        "partner_invoice_registrations" =>
            "INSERT INTO partner_invoice_registrations (partner_id, registration_no, valid_from)"
            + " VALUES (1, 'T1234567890123', '2023-10-01');",
        _ => string.Empty,
    };

    /// <summary>稼働しているトリガの定義文。<b>規則の写しを持たないための口</b>。</summary>
    private static string Definition(SqliteConnection db, string table, string kind)
        => TestDatabase.ScalarOf<string>(
            db,
            "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
            + $" AND name = 'trg_{table}_date_format_{kind}'");
}
