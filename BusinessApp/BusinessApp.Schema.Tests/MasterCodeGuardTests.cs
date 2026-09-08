namespace BusinessApp.Schema.Tests;

using System.Text.RegularExpressions;

using BusinessApp.ServerSupport;
using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// マスタのコードの書式（docs/12 §2-1・ADR-0047）を DDL のトリガが守る。
/// </summary>
/// <remarks>
/// <para><b>関門（<see cref="MasterCode"/>）と同じ広さで守る。</b> 二層に置く狙いは
/// <b>関門が走らない経路</b>（CSV 取込・<c>sql</c> CLI・手作業の SQL）を止めることなので、
/// 広さがずれると片方だけが通す穴になる（qa/03 L-14 の型）。
/// <see cref="トリガが通す字は関門が通す字と過不足なく一致する"/> が毎回それを確かめる。</para>
/// <para><b>規則を書き写さない。</b> 稼働しているトリガの定義を <c>sqlite_master</c> から読み、
/// その <c>WHEN</c> 節をそのまま評価する。写すと、正典を直したときに写しだけが古くなる。</para>
/// </remarks>
public class MasterCodeGuardTests
{
    /// <summary>コードを持つ 6 つの表（docs/12 §2-1）。</summary>
    public static TheoryData<string> Tables => new()
    {
        "fiscal_years", "tax_categories", "accounts", "sub_accounts", "departments", "partners",
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void コードを持つ6つの表すべてに追加と更新のトリガがある(string table)
    {
        using var db = SchemaSeed.Create();

        Assert.Equal(
            [$"trg_{table}_code_format_insert", $"trg_{table}_code_format_update"],
            TestDatabase.Query(
                db,
                "SELECT name FROM sqlite_master WHERE type = 'trigger'"
                + $" AND tbl_name = '{table}' AND name LIKE '%code_format%' ORDER BY name"));
    }

    /// <summary>
    /// <b>12 本のトリガの <c>WHEN</c> 節が、1 字も違わず同じである。</b>
    /// </summary>
    /// <remarks>
    /// 下の総当たりは <c>accounts</c> の 1 本しか読まない（65,535 個の符号位置を 6 表 × 2 で撃つと遅い）。
    /// <b>残る 11 本は「同じ字であること」で担保する</b>——`departments` の `'*[-_][-_]*'` を
    /// 1 文字打ち間違えても、`update` の条件が 1 つ欠けても、ここが赤くなる
    /// （2026-09-09 の自己レビューで、1 本しか見ていないことを指摘された）。
    /// </remarks>
    [Fact]
    public void トリガ12本は同じ条件を持つ()
    {
        using var db = SchemaSeed.Create();

        var clauses = Tables.SelectMany(row => new[] { "insert", "update" }
            .Select(kind => (Table: (string)row[0], Kind: kind,
                             Clause: WhenClause(db, (string)row[0], kind))))
            .ToList();

        Assert.Equal(12, clauses.Count);
        Assert.All(clauses, c => Assert.Equal(clauses[0].Clause, c.Clause));
    }

    /// <summary>断りの文言も 12 本で揃っていて、書式の説明を含む。</summary>
    /// <remarks>
    /// <b>この文言が届くのは <c>sql</c> CLI と取込だけである</b>——アプリの経路では
    /// <c>SaveFailureMessage</c> が定型文へ差し替える（2026-09-09 に確かめた）。
    /// それでも揃えるのは、<b>CLI で流す人にも直し方が要る</b>からである。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Tables))]
    public void 断りの文言は書式の説明を含む(string table)
    {
        using var db = SchemaSeed.Create();

        foreach (var kind in new[] { "insert", "update" })
        {
            var definition = TestDatabase.ScalarOf<string>(
                db,
                "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
                + $" AND name = 'trg_{table}_code_format_{kind}'");

            Assert.Contains("半角の英数字と「-」「_」", definition, StringComparison.Ordinal);
            Assert.Contains(
                MasterCode.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 文字以内",
                definition,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>トリガが通す字と、関門が通す字が過不足なく一致する。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>両方向を見る。</b> トリガのほうが狭いと、関門を通った値を DB が拒む——
    /// 利用者には枠組みの言葉が出る（qa/01 F-16）。広いと、関門を迂回した経路で
    /// 見分けの付かないコードが入る（qa/03 L-32 がそれである）。</para>
    /// <para><b>字を並べた一覧を持たない。</b> 符号位置を端から当てるので、
    /// どちらかに字を足し忘れたら必ず赤くなる。<b>サロゲートの範囲は外す</b>——
    /// 単独では文字にならず、<c>char()</c> が返すものが C# の <c>char</c> 1 つと対応しない。</para>
    /// <para><b>U+0000 も当てる。</b> <c>LENGTH</c> と <c>GLOB</c> は文字列の途中の NUL で止まるので、
    /// 字種の GLOB だけでは <c>A</c>‖NUL‖<c>B</c> が「使える字」と答える（2026-09-09 に実測）。
    /// トリガの最後の条件（バイト数と文字数の突き合わせ）がここで効いている。
    /// <b>0 を外しておくと、その穴はこの総当たりからも隠れる</b>——外していた
    /// （2026-09-09 の自己レビュー）。</para>
    /// </remarks>
    [Fact]
    public void トリガが通す字は関門が通す字と過不足なく一致する()
    {
        using var db = SchemaSeed.Create();

        // 「A ＋ 調べる字 ＋ B」で当てる。**先頭・末尾の規則に引っかからない位置**に置くので、
        // ここで見えるのは字種の判定だけである（先頭末尾と連続は下の Theory が見る）。
        var accepted = new HashSet<int>(TestDatabase.Query(
            db,
            $"""
            WITH RECURSIVE code(n) AS (SELECT 0 UNION ALL SELECT n + 1 FROM code WHERE n < {(int)char.MaxValue})
            SELECT n FROM code
             WHERE (n < 55296 OR n > 57343)
               AND NOT ({WhenClause(db, "accounts").Replace("NEW.code", "('A' || char(n) || 'B')", StringComparison.Ordinal)})
            """).Select(int.Parse));

        var expected = new HashSet<int>(
            Enumerable.Range(0, char.MaxValue + 1)
                .Where(n => (n < 55296 || n > 57343)
                            && MasterCode.DescribeProblem("科目コード", "A" + (char)n + "B") is null));

        Assert.Equal(expected.OrderBy(n => n), accepted.OrderBy(n => n));
    }

    /// <summary>
    /// 形の規則（先頭・末尾・連続・長さ）も、トリガと関門で一致する。
    /// </summary>
    /// <remarks>
    /// 字種と違って符号位置で総当たりできないので、<b>境界を並べる</b>。
    /// <b>関門の判定をそのまま期待値に使う</b>ので、規則を 2 度書かない。
    /// </remarks>
    [Theory]
    [InlineData("1100")]
    [InlineData("A")]
    [InlineData("12345678901234567890")]      // 上限ちょうど
    [InlineData("123456789012345678901")]     // 上限の 1 つ上
    [InlineData("-100")]
    [InlineData("100-")]
    [InlineData("_100")]
    [InlineData("100_")]
    [InlineData("-")]
    [InlineData("1--2")]
    [InlineData("1__2")]
    [InlineData("1-_2")]
    [InlineData("1-2-3")]
    [InlineData("a-b_c")]
    [InlineData("")]
    public void 形の規則もトリガと関門で一致する(string code)
    {
        using var db = SchemaSeed.Create();

        var literal = "'" + code.Replace("'", "''", StringComparison.Ordinal) + "'";
        var rejectedByTrigger = TestDatabase.ScalarOf<long>(
            db,
            $"SELECT {WhenClause(db, "accounts").Replace("NEW.code", literal, StringComparison.Ordinal)}") == 1;

        // **空は関門では「必須」の側が扱う**ので、ここだけ期待値が違う（ADR-0047 の決定 10）。
        var rejectedByGate = code.Length == 0 || MasterCode.DescribeProblem("科目コード", code) is not null;

        Assert.Equal(rejectedByGate, rejectedByTrigger);
    }

    /// <summary>
    /// <b>BLOB のコードは断る。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>BLOB は TEXT の列にそのまま入る</b>——STRICT ではないので affinity が効かず、
    /// 数値は text へ変換されるのに <b>BLOB だけは BLOB のまま残る</b>（2026-09-09 に実測）。
    /// そして <c>GLOB</c> は BLOB に「使える字」と答える。</para>
    /// <para><b>そのままだと qa/03 L-32 が別の入口から開き直る</b>——
    /// <c>x'4142'</c> と <c>'AB'</c> は<b>別の値</b>なので <c>COLLATE NOCASE</c> の一意索引でもぶつからず、
    /// <b>見た目が同じ 2 行が並ぶ</b>。<c>x'41004200'</c> は NUL の条件もすり抜ける
    /// （BLOB では <c>LENGTH</c> も <c>CAST(… AS BLOB)</c> もバイト数を返して必ず等しくなる）。
    /// 2026-09-09 の自己レビューで見つけた。</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Tables))]
    public void BLOB_のコードは追加できない(string table)
    {
        using var db = SchemaSeed.Create();

        foreach (var blob in new[] { "x'4142'", "x'41004200'" })
        {
            var thrown = Assert.Throws<SqliteException>(
                () => TestDatabase.Execute(db, Insert(table, blob, quoted: false)));

            Assert.Contains("半角の英数字と「-」「_」", thrown.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, $"SELECT COUNT(*) FROM {table} WHERE typeof(code) = 'blob'"));
    }

    /// <summary>BLOB のコードへは更新もできない。</summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void BLOB_のコードへは更新できない(string table)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, Insert(table, "Z999"));

        var thrown = Assert.Throws<SqliteException>(
            () => TestDatabase.Execute(db, $"UPDATE {table} SET code = x'4142' WHERE code = 'Z999'"));

        Assert.Contains("半角の英数字と「-」「_」", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("Z999", TestDatabase.ScalarOf<string>(db, $"SELECT code FROM {table} WHERE id = (SELECT MAX(id) FROM {table})"));
    }

    /// <summary>
    /// 書式に反するコードは追加できない。
    /// </summary>
    /// <remarks>
    /// <b>例外の型だけを見ない。</b> 型だけだと、外部キー違反や NOT NULL 違反でも同じ
    /// <see cref="SqliteException"/> が出て緑になる（qa/03 L-02 の縮退。2026-09-09 の自己レビュー）。
    /// <b>書式のトリガが鳴ったことを、文言で特定する。</b>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Tables))]
    public void 書式に反するコードは追加できない(string table)
    {
        using var db = SchemaSeed.Create();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Insert(table, "1100 ")));

        Assert.Contains("半角の英数字と「-」「_」", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void 書式に反するコードへは更新できない(string table)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, Insert(table, "Z999"));

        var thrown = Assert.Throws<SqliteException>(
            () => TestDatabase.Execute(db, $"UPDATE {table} SET code = '１１００' WHERE code = 'Z999'"));

        Assert.Contains("半角の英数字と「-」「_」", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 大小を無視した重複は、一意索引が止める（ADR-0047 の決定 7）。
    /// </summary>
    /// <remarks>
    /// <b>保存された字は入力のまま</b>である——畳むのは判定だけで、値は書き換えない。
    /// </remarks>
    [Theory]
    [MemberData(nameof(Tables))]
    public void 大小だけが違うコードは重ねられない(string table)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, Insert(table, "Z999a"));

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Insert(table, "Z999A")));
        Assert.Equal("Z999a", TestDatabase.ScalarOf<string>(db, $"SELECT code FROM {table} WHERE code LIKE 'Z999%'"));
    }

    /// <summary>稼働しているトリガの <c>WHEN</c> 節。<b>規則の写しを持たないための口</b>。</summary>
    private static string WhenClause(SqliteConnection db, string table, string kind = "insert")
    {
        var definition = TestDatabase.ScalarOf<string>(
            db,
            "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
            + $" AND name = 'trg_{table}_code_format_{kind}'");
        var when = Regex.Match(definition, @"WHEN\s+(.+?)\s*BEGIN", RegexOptions.Singleline);

        Assert.True(when.Success, "トリガの WHEN 節を読めない。定義の書き方を変えたら、ここも直す。");
        return when.Groups[1].Value;
    }

    /// <summary>表ごとに要る列だけを埋めて 1 行入れる。</summary>
    /// <summary>
    /// コードを 1 つ入れる SQL。
    /// </summary>
    /// <remarks>
    /// <paramref name="quoted"/> を <c>false</c> にすると <paramref name="code"/> を<b>そのまま式として置く</b>
    /// （<c>x'4142'</c> のような BLOB のリテラルを渡すため）。
    /// </remarks>
    private static string Insert(string table, string code, bool quoted = true)
    {
        var value = quoted ? "'" + code + "'" : code;
        return table switch
        {
            "fiscal_years" =>
                $"INSERT INTO fiscal_years (code, label, start_date, end_date, status)"
                + $" VALUES ({value}, '検証', '2027-04-01', '2028-03-31', 'open')",
            "tax_categories" => $"INSERT INTO tax_categories (code, name, taxation_type) VALUES ({value}, '検証', 'out_of_scope')",
            "accounts" => $"INSERT INTO accounts (code, name, category) VALUES ({value}, '検証', 'asset')",
            // **親をコードで引く。** id をベタ書きすると、シードの並びが変わった日に
            // 外部キー違反でも同じ例外が出て、書式のトリガを撃ったつもりで緑になる（qa/03 L-02）。
            "sub_accounts" =>
                $"INSERT INTO sub_accounts (account_id, code, name)"
                + $" VALUES ((SELECT id FROM accounts WHERE code = '1100'), {value}, '検証')",
            "departments" => $"INSERT INTO departments (code, name) VALUES ({value}, '検証')",
            "partners" => $"INSERT INTO partners (code, name) VALUES ({value}, '検証')",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "コードを持たない表"),
        };
    }
}
