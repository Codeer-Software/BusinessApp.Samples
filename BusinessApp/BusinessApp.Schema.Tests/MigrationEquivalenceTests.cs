namespace BusinessApp.Schema.Tests;

using System.Text.RegularExpressions;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// マイグレーションの同値検査（ADR-0020）。「baseline ＋ 未掃除のマイグレーション」を再生した DB が
/// 「ddl/（現在形の正典）」から作った DB と一致することを、稼働 DB に触る前（コミット前フック）に確かめる。
/// <b>正典を書き換えたのにマイグレーションを書き忘れた・間違えた</b>を捕まえる網である。
/// </summary>
/// <remarks>
/// <para>比較は <see cref="SchemaSnapshot"/>（正規化テキストの完全一致）。稼働 DB 側の検証
/// （<c>migrate.ps1 -Verify</c>）も同じ比較を使う。比較器そのものの検査（赤くなる経路）は
/// <see cref="SchemaSnapshotTests"/> にある。利用者が編集できないテーブルの行（制度ルール）の
/// 同値検査は、そのテーブルが生まれるフェーズ 3 でここに足す。</para>
/// <para><b>再生条件はランナーに合わせる。</b> ランナー（<c>migrate.ps1 -Apply</c>）は
/// マイグレーション 1 本を 1 つの BEGIN/COMMIT で包み、外部キーは ON（2026-08-25 実測。
/// <c>sql</c> CLI の接続で <c>PRAGMA foreign_keys</c> = 1）。ここも同じ条件で再生する。
/// SQLite の <c>PRAGMA foreign_keys</c> は<b>トランザクション内では黙って無視される</b>ので、
/// 条件を揃えないと「テストでは効いて緑・本番では無視されて失敗」が起きる。</para>
/// </remarks>
public class MigrationEquivalenceTests
{
    /// <summary>ランナーと同じ包み方でマイグレーションを再生した DB を返す。</summary>
    private static SqliteConnection ReplayBaselinePlusMigrations()
    {
        var db = TestDatabase.CreateFromFiles(TestDatabase.BaselineFiles());
        foreach (var file in TestDatabase.MigrationFiles())
        {
            TestDatabase.Execute(db, $"BEGIN;\n{File.ReadAllText(file)}\nCOMMIT;");
        }

        return db;
    }

    [Fact]
    public void Baselineとマイグレーションの再生は正典と同値である()
    {
        using var expected = TestDatabase.Create();
        using var actual = ReplayBaselinePlusMigrations();

        var diff = SchemaSnapshot.Diff(
            SchemaSnapshot.Dump(expected),
            SchemaSnapshot.Dump(actual),
            "正典(ddl)",
            "baseline+migrations");

        Assert.True(diff.Count == 0, string.Join("\n", diff));
    }

    [Fact]
    public void BaselineのVERSIONは畳んだ番号を整数で持つ()
    {
        var versionFile = Path.Combine(TestDatabase.BaselineDirectory, "VERSION");

        Assert.True(File.Exists(versionFile), "baseline/VERSION が無い。");
        Assert.True(
            int.TryParse(File.ReadAllText(versionFile).Trim(), out var version) && version >= 0,
            "baseline/VERSION は 0 以上の整数 1 つでなければならない。");
    }

    [Fact]
    public void マイグレーションのファイル名は連番と小文字スネークケースである()
    {
        foreach (var file in TestDatabase.MigrationFiles())
        {
            Assert.Matches(new Regex(@"^\d{4}_[a-z0-9_]+\.sql$"), Path.GetFileName(file));
        }
    }

    /// <summary>
    /// 番号は baseline の VERSION の次から欠番なく続く。飛び番は付け間違いのサインであり、
    /// 掃除（ADR-0020 §4）は「VERSION 以前を消す」なので正しく運用すれば飛ばない。
    /// </summary>
    [Fact]
    public void マイグレーションの番号はVERSIONの次から欠番なく続く()
    {
        var baselineVersion = int.Parse(
            File.ReadAllText(Path.Combine(TestDatabase.BaselineDirectory, "VERSION")).Trim());
        var versions = TestDatabase.MigrationFiles()
            .Select(f => int.Parse(Path.GetFileName(f)[..4]))
            .ToList();

        var expected = Enumerable.Range(baselineVersion + 1, versions.Count).ToList();
        Assert.Equal(expected, versions);
    }

    [Fact]
    public void マイグレーションは自分でトランザクションを張らない()
    {
        foreach (var file in TestDatabase.MigrationFiles())
        {
            Assert.False(
                ContainsTransactionStatement(File.ReadAllText(file)),
                $"{Path.GetFileName(file)}: BEGIN/COMMIT/ROLLBACK/SAVEPOINT を書かない。トランザクションはランナーが張る。");
        }
    }

    /// <summary>
    /// PRAGMA はトランザクション内では効かないか（<c>foreign_keys</c> / <c>journal_mode</c> は
    /// 黙って no-op）、効いてはいけないもの（<c>writable_schema</c>）なので機械で拒む。
    /// 例外は <c>defer_foreign_keys</c>（作り直しに必要で、トランザクション内でも効く）。
    /// <c>migrate.ps1</c> の -Apply と鏡写し。
    /// </summary>
    [Fact]
    public void マイグレーションはPRAGMAを書かない()
    {
        foreach (var file in TestDatabase.MigrationFiles())
        {
            Assert.False(
                ContainsForbiddenPragma(File.ReadAllText(file)),
                $"{Path.GetFileName(file)}: PRAGMA は defer_foreign_keys 以外書かない。");
        }
    }

    /// <summary>
    /// 最後の文が <c>;</c> で終わっていないと、ランナーが後ろに連結する記録の INSERT が
    /// 前の文に混ざって壊れる。ランナー（<c>migrate.ps1</c>）と同じ判定
    /// （末尾の空白を除いて <c>;</c> で終わる）にしてあるので、<b>最後の文の後ろにコメントを置かない</b>。
    /// </summary>
    [Fact]
    public void マイグレーションは文の終端で終わる()
    {
        foreach (var file in TestDatabase.MigrationFiles())
        {
            Assert.True(
                File.ReadAllText(file).TrimEnd().EndsWith(';'),
                $"{Path.GetFileName(file)}: 末尾が ; で終わっていない（最後の文の後ろにコメントを置かない）。");
        }
    }

    // ---- 判定ロジックそのものの検査（マイグレーションが 0 本でも網が生きていることを示す） ----

    /// <summary>
    /// トランザクション文の検出。<c>tools/clb/migrate.ps1</c> の -Apply と鏡写し
    /// （片方だけ直すと網がずれる）。行頭のみ見るのはトリガ本体の
    /// <c>BEGIN ... END;</c> を誤検出しないため。素の <c>END;</c>（COMMIT の同義語）は
    /// トリガ終端と字面で区別できないので拒まない。この網をすり抜けた文は、
    /// ランナーの BEGIN/COMMIT と衝突して再生（上の同値テスト）が落ちるか、
    /// ランナーの「適用後の記録行の存在確認」が捕まえるので、静かには通らない。
    /// </summary>
    private static bool ContainsTransactionStatement(string sql)
        => Regex.IsMatch(
            sql,
            @"^\s*(BEGIN(\s+(DEFERRED|IMMEDIATE|EXCLUSIVE))?(\s+TRANSACTION)?|COMMIT(\s+TRANSACTION)?|ROLLBACK(\s+TRANSACTION)?(\s+TO\s+\S+)?|SAVEPOINT\s+\S+|RELEASE(\s+SAVEPOINT)?\s+\S+)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary><c>migrate.ps1</c> の -Apply と鏡写し。</summary>
    private static bool ContainsForbiddenPragma(string sql)
        => Regex.IsMatch(sql, @"^\s*PRAGMA\s+(?!defer_foreign_keys\b)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    [Theory]
    [InlineData("PRAGMA foreign_keys = OFF;\nDROP TABLE x;")]
    [InlineData("SELECT 1;\npragma writable_schema = ON;")]
    [InlineData("PRAGMA journal_mode = DELETE;")]
    public void 禁止されたPRAGMAを検出する(string sql)
    {
        Assert.True(ContainsForbiddenPragma(sql));
    }

    [Theory]
    [InlineData("PRAGMA defer_foreign_keys = ON;\nDROP TABLE x;")]
    [InlineData("SELECT 'PRAGMA foreign_keys' AS s;")]
    public void 作り直しに要るPRAGMAと引用内の字句は許す(string sql)
    {
        Assert.False(ContainsForbiddenPragma(sql));
    }

    [Theory]
    [InlineData("BEGIN;\nALTER TABLE t ADD COLUMN a TEXT;\nCOMMIT;")]
    [InlineData("BEGIN TRANSACTION;\nSELECT 1;")]
    [InlineData("BEGIN IMMEDIATE;\nSELECT 1;")]
    [InlineData("SELECT 1;\nCOMMIT TRANSACTION;")]
    [InlineData("SELECT 1;\nROLLBACK;")]
    [InlineData("SAVEPOINT sp1;\nSELECT 1;")]
    [InlineData("RELEASE sp1;")]
    public void トランザクション文の検出は代表的な書き方をすべて拒む(string sql)
    {
        Assert.True(ContainsTransactionStatement(sql));
    }

    [Theory]
    [InlineData("ALTER TABLE t ADD COLUMN a TEXT;")]
    [InlineData("CREATE TRIGGER trg BEFORE UPDATE ON t FOR EACH ROW\nBEGIN\n    SELECT RAISE(ABORT, 'x');\nEND;")]
    [InlineData("-- BEGIN; と書いたコメント行は文ではない\nSELECT 1;")]
    public void トリガ本体や通常の文は誤検出しない(string sql)
    {
        Assert.False(ContainsTransactionStatement(sql));
    }
}
