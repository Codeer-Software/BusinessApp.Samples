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
    /// <b>トリガが通す字と、関門が通す字が過不足なく一致する。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>両方向を見る。</b> トリガのほうが狭いと、関門を通った値を DB が拒む——
    /// 利用者には枠組みの言葉が出る（qa/01 F-16）。広いと、関門を迂回した経路で
    /// 見分けの付かないコードが入る（qa/03 L-32 がそれである）。</para>
    /// <para><b>字を並べた一覧を持たない。</b> 符号位置を端から当てるので、
    /// どちらかに字を足し忘れたら必ず赤くなる。<b>サロゲートの範囲は外す</b>——
    /// 単独では文字にならず、<c>char()</c> が返すものが C# の <c>char</c> 1 つと対応しない。</para>
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
            WITH RECURSIVE code(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM code WHERE n < {(int)char.MaxValue})
            SELECT n FROM code
             WHERE (n < 55296 OR n > 57343)
               AND NOT ({WhenClause(db, "accounts").Replace("NEW.code", "('A' || char(n) || 'B')", StringComparison.Ordinal)})
            """).Select(int.Parse));

        var expected = new HashSet<int>(
            Enumerable.Range(1, char.MaxValue)
                .Where(n => (n < 55296 || n > 57343)
                            && MasterCode.DescribeProblem("A" + (char)n + "B") is null));

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
        var rejectedByGate = code.Length == 0 || MasterCode.DescribeProblem(code) is not null;

        Assert.Equal(rejectedByGate, rejectedByTrigger);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void 書式に反するコードは追加できない(string table)
    {
        using var db = SchemaSeed.Create();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Insert(table, "1100 ")));
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void 書式に反するコードへは更新できない(string table)
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, Insert(table, "Z999"));

        Assert.Throws<SqliteException>(
            () => TestDatabase.Execute(db, $"UPDATE {table} SET code = '１１００' WHERE code = 'Z999'"));
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
    private static string WhenClause(SqliteConnection db, string table)
    {
        var definition = TestDatabase.ScalarOf<string>(
            db,
            "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
            + $" AND name = 'trg_{table}_code_format_insert'");
        var when = Regex.Match(definition, @"WHEN\s+(.+?)\s*BEGIN", RegexOptions.Singleline);

        Assert.True(when.Success, "トリガの WHEN 節を読めない。定義の書き方を変えたら、ここも直す。");
        return when.Groups[1].Value;
    }

    /// <summary>表ごとに要る列だけを埋めて 1 行入れる。</summary>
    private static string Insert(string table, string code) => table switch
    {
        "fiscal_years" =>
            $"INSERT INTO fiscal_years (code, label, start_date, end_date, status)"
            + $" VALUES ('{code}', '検証', '2027-04-01', '2028-03-31', 'open')",
        "tax_categories" => $"INSERT INTO tax_categories (code, name, taxation_type) VALUES ('{code}', '検証', 'out_of_scope')",
        "accounts" => $"INSERT INTO accounts (code, name, category) VALUES ('{code}', '検証', 'asset')",
        "sub_accounts" => $"INSERT INTO sub_accounts (account_id, code, name) VALUES (1, '{code}', '検証')",
        "departments" => $"INSERT INTO departments (code, name) VALUES ('{code}', '検証')",
        "partners" => $"INSERT INTO partners (code, name) VALUES ('{code}', '検証')",
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "コードを持たない表"),
    };
}
