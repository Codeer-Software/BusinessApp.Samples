namespace BusinessApp.Schema.Tests;

using System.Text.RegularExpressions;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 摘要のない仕訳は計上できない（docs/10 §4-2-1。法税規則 55 ① の仕訳帳の記載事項「内容」）。
/// </summary>
/// <remarks>
/// <para><b>関門（<c>JournalEntryValidator</c>）と同じ広さで守る。</b> 二層に置く狙いは
/// <b>関門が走らない経路</b>（CSV 取込・<c>sql</c> CLI・手作業の SQL）を止めることなので、
/// トリガのほうが狭いと「関門なら弾く値」が帳簿に載る。
/// <see cref="空白だけの摘要はどの空白文字でも計上できない"/> が両者の同値を毎回確かめる。</para>
/// <para><b>下書きには求めない</b>（開発者の決定。2026-09-05）。
/// 条文が求めるのは帳簿への記載なので、計上を止めれば記載事項を欠いた行は帳簿に載らない。</para>
/// </remarks>
public class JournalDescriptionGuardTests
{
    private const string Draft = """
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            VALUES (1, '2026-05-20', '2026-05-20', 'draft', 'normal', '5 月分の現金売上', '2026-05-20 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 1, 'debit', 1, 100000, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
            VALUES (1, 2, 'credit', 2, 2, 100000, 1);
        """;

    private const string Post =
        "UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00' WHERE id = 1";

    /// <summary>
    /// <c>char.IsWhiteSpace</c> が真になる符号位置を全部通す。
    /// </summary>
    /// <remarks>
    /// <b>DDL の <c>trim</c> に並べた字と、関門が使う <c>string.IsNullOrWhiteSpace</c> の同値を、
    /// 一覧を書き写さずに突き合わせる。</b> どちらかに字を足し忘れたら、ここが赤くなる。
    /// </remarks>
    public static TheoryData<int> WhiteSpaceCodePoints()
    {
        var data = new TheoryData<int>();
        for (var code = 0; code <= char.MaxValue; code++)
        {
            if (char.IsWhiteSpace((char)code))
            {
                data.Add(code);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WhiteSpaceCodePoints))]
    public void 空白だけの摘要はどの空白文字でも計上できない(int code)
    {
        using var db = Blocked($"char({code})");

        Assert.Equal("draft", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
    }

    /// <summary>
    /// トリガが「空白」とみなす字が、<c>char.IsWhiteSpace</c> と<b>過不足なく一致する</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>上の Theory は「空白なら止まる」しか見ない。</b> トリガの <c>trim</c> に
    /// **空白でない字**を紛れ込ませても赤くならず、そのときは
    /// <b>関門を通った値を DB が拒む</b>——枠組みの言葉で失敗する形（qa/01 F-16）に戻る。
    /// だから<b>両方向</b>を見る。</para>
    /// <para><b>字の一覧は書き写さない。</b> 稼働しているトリガの定義を <c>sqlite_master</c> から読み、
    /// その <c>char(...)</c> をそのまま使う。写すと、正典を直したときに写しだけが古くなる。</para>
    /// <para><b>サロゲートの範囲は外す</b>——単独では文字にならず、
    /// <c>char()</c> が何を返すかは C# の <c>char</c> 1 つと対応しない。</para>
    /// </remarks>
    [Fact]
    public void トリガが空とみなす字は_char_IsWhiteSpace_と過不足なく一致する()
    {
        using var db = SchemaSeed.Create();

        var definition = TestDatabase.ScalarOf<string>(
            db,
            "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
            + " AND name = 'trg_journal_entries_description_required_when_posted'");
        var chars = Regex.Match(definition, @"char\(([\d,\s]+)\)", RegexOptions.Singleline);
        Assert.True(chars.Success, "トリガの char(...) を読めない。定義の書き方を変えたら、ここも直す。");

        var blank = new HashSet<int>(TestDatabase.Query(
            db,
            $"""
            WITH RECURSIVE code(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM code WHERE n < {(int)char.MaxValue})
            SELECT n FROM code
             WHERE (n < 55296 OR n > 57343)
               AND trim(char(n), char({chars.Groups[1].Value})) = ''
            """).Select(int.Parse));

        var expected = new HashSet<int>(
            Enumerable.Range(1, char.MaxValue)
                .Where(n => (n < 55296 || n > 57343) && char.IsWhiteSpace((char)n)));

        Assert.Equal(expected.OrderBy(n => n), blank.OrderBy(n => n));
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("''")]
    [InlineData("' ' || char(12288) || char(9)")]     // 種類を混ぜても同じ
    public void 摘要が空のままでは計上できない(string description)
    {
        using var db = Blocked(description);

        Assert.Equal("draft", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
    }

    [Fact]
    public void 摘要が入っていれば計上できる()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, Draft);
        TestDatabase.Execute(db, "UPDATE journal_entries SET description = '  5 月分の現金売上  ' WHERE id = 1");

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
    }

    [Fact]
    public void 下書きのままなら摘要を空白に変えられる()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, Draft);

        // **打ちかけの伝票を止めない。** ここが赤くなったら、下書きにまで必須を広げてしまっている。
        TestDatabase.Execute(db, "UPDATE journal_entries SET description = char(12288) WHERE id = 1");

        // **打った空白がそのまま入る**（NULL に直されない。qa/01 A-11）。
        // 値を読み戻すところまで見ないと、UPDATE が 0 行にしか当たらなくても緑になる。
        Assert.Equal("　", TestDatabase.ScalarOf<string>(db, "SELECT description FROM journal_entries WHERE id = 1"));
        Assert.Equal("draft", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
    }

    /// <summary>摘要を <paramref name="description"/> にした下書きの計上が、断られることを確かめる。</summary>
    private static SqliteConnection Blocked(string description)
    {
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db, Draft);
        TestDatabase.Execute(db, $"UPDATE journal_entries SET description = {description} WHERE id = 1");

        // **SqliteException でなければ落とす。** ThrowsAny だとヘルパ側の NullReference でも緑になる。
        var error = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));
        Assert.Contains("摘要のない仕訳は計上できない", error.Message, StringComparison.Ordinal);
        return db;
    }
}
