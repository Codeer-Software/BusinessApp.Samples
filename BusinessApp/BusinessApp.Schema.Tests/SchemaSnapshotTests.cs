namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// スキーマ比較器そのものの検査。同値テストと <c>-Verify</c> の安全性はこの比較器が
/// 「正しく赤くなること」に全面依存するのに、同値テスト（baseline ≡ ddl）は現状ほぼ恒等比較で、
/// 壊れた比較器でも緑になってしまう（2026-08-25 の自己レビュー指摘）。ここで赤の経路を直接固定する。
/// </summary>
public class SchemaSnapshotTests
{
    private static SqliteConnection Memory()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        return db;
    }

    // ---- NormalizeSql ----

    [Theory]
    [InlineData("CREATE TABLE t (\n  id INTEGER, -- コメント\n  v TEXT\n)", "CREATE TABLE t ( id INTEGER, v TEXT )")]
    [InlineData("CREATE TABLE t (id INTEGER /* ブロック\nコメント */, v TEXT)", "CREATE TABLE t (id INTEGER , v TEXT)")]
    [InlineData("SELECT  1\t+\n 2", "SELECT 1 + 2")]
    public void 正規化はコメントを除き空白を1つに潰す(string input, string expected)
    {
        Assert.Equal(expected, SchemaSnapshot.NormalizeSql(input));
    }

    [Theory]
    [InlineData("SELECT 'a  b'", "SELECT 'a  b'")]                       // 引用内の空白は潰さない
    [InlineData("SELECT 'a -- b'", "SELECT 'a -- b'")]                   // 引用内の -- はコメントではない
    [InlineData("SELECT 'it''s', 1", "SELECT 'it''s', 1")]               // 二重クォートのエスケープ
    [InlineData("SELECT \"my  col\" FROM t", "SELECT \"my  col\" FROM t")]
    [InlineData("SELECT [my  col] FROM t", "SELECT [my  col] FROM t")]
    public void 正規化は引用の中を変えない(string input, string expected)
    {
        Assert.Equal(expected, SchemaSnapshot.NormalizeSql(input));
    }

    [Fact]
    public void 正規化は未終端のブロックコメントでも落ちない()
    {
        Assert.Equal("SELECT 1", SchemaSnapshot.NormalizeSql("SELECT 1 /* 未終端"));
    }

    // ---- Diff の赤の経路 ----

    [Fact]
    public void 欠けているオブジェクトを報告する()
    {
        var expected = new[] { new SchemaObject("table", "a", "CREATE TABLE a (id INTEGER)") };

        var diff = SchemaSnapshot.Diff(expected, [], "正", "稼働");

        var line = Assert.Single(diff);
        Assert.Equal("稼働 に無い: table a", line);
    }

    [Fact]
    public void 余分なオブジェクトを報告する()
    {
        var actual = new[] { new SchemaObject("trigger", "trg_x", "CREATE TRIGGER trg_x ...") };

        var diff = SchemaSnapshot.Diff([], actual, "正", "稼働");

        var line = Assert.Single(diff);
        Assert.Equal("正 に無い: trigger trg_x", line);
    }

    [Fact]
    public void 定義の違いを両方のテキストつきで報告する()
    {
        var expected = new[] { new SchemaObject("table", "a", "CREATE TABLE a (id INTEGER)") };
        var actual = new[] { new SchemaObject("table", "a", "CREATE TABLE a (id INTEGER, v TEXT)") };

        var diff = SchemaSnapshot.Diff(expected, actual, "正", "稼働");

        var line = Assert.Single(diff);
        Assert.StartsWith("定義が違う: table a", line);
        Assert.Contains("CREATE TABLE a (id INTEGER)", line);
        Assert.Contains("CREATE TABLE a (id INTEGER, v TEXT)", line);
    }

    [Fact]
    public void 同じスキーマなら差は無い()
    {
        var objects = new[] { new SchemaObject("table", "a", "CREATE TABLE a (id INTEGER)") };

        Assert.Empty(SchemaSnapshot.Diff(objects, objects, "正", "稼働"));
    }

    // ---- Dump / FromRows ----

    [Fact]
    public void DumpはSQLiteの内部オブジェクトとランナーの記録テーブルを除く()
    {
        using var db = Memory();
        TestDatabase.Execute(db, """
            CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, v TEXT UNIQUE);
            CREATE TABLE schema_migrations (version INTEGER PRIMARY KEY);
            """);

        var names = SchemaSnapshot.Dump(db).Select(o => o.Name);

        // sqlite_sequence（AUTOINCREMENT の内部表）・sqlite_autoindex（UNIQUE の内部索引）・
        // schema_migrations（ランナー管理。正典の外にあるのが正しい）は比較しない。
        Assert.Equal(["t"], names);
    }

    [Fact]
    public void 除外はテーブルに限り同名のトリガは検査に残る()
    {
        var objects = SchemaSnapshot.FromRows(
        [
            ("table", "schema_migrations", "CREATE TABLE schema_migrations (version INTEGER)"),
            ("trigger", "schema_migrations", "CREATE TRIGGER schema_migrations BEFORE UPDATE ON t BEGIN SELECT 1; END"),
        ]);

        var obj = Assert.Single(objects);
        Assert.Equal("trigger", obj.Type);
    }

    /// <summary>
    /// 「列の追加は正典でも列リストの末尾（テーブル制約の前）に置けば、ADD COLUMN と
    /// テキスト同値になる」という規約の根拠（2026-08-25 実測）を、テストに昇格させて固定する。
    /// SQLite の版が変わって挿入位置が変われば、ここが落ちて規約の見直しを迫る。
    /// </summary>
    [Fact]
    public void ADDCOLUMNで書き換わった格納テキストは列を末尾に置いた正典と同値になる()
    {
        using var migrated = Memory();
        TestDatabase.Execute(migrated, """
            CREATE TABLE t (
                id INTEGER PRIMARY KEY,
                a  TEXT NOT NULL,   -- 既存列
                CHECK (a <> '')
            );
            ALTER TABLE t ADD COLUMN b TEXT;
            """);

        using var canonical = Memory();
        TestDatabase.Execute(canonical, """
            CREATE TABLE t (
                id INTEGER PRIMARY KEY,
                a  TEXT NOT NULL,   -- 既存列
                b TEXT,
                CHECK (a <> '')
            );
            """);

        var diff = SchemaSnapshot.Diff(
            SchemaSnapshot.Dump(canonical), SchemaSnapshot.Dump(migrated), "正典", "移行後");

        Assert.True(diff.Count == 0, string.Join("\n", diff));
    }

    // ---- 管轄（SplitByJurisdiction） ----

    private static readonly IReadOnlyList<SchemaObject> Canon =
    [
        new("index", "ix_a_v", "CREATE INDEX ix_a_v ON a (v)"),
        new("table", "a", "CREATE TABLE a (id INTEGER, v TEXT)"),
    ];

    [Fact]
    public void 他部品のテーブルと付属物は管轄外として比較しない()
    {
        var (inScope, ignored) = SchemaSnapshot.SplitByJurisdiction(Canon,
        [
            ("table", "a", "a", "CREATE TABLE a (id INTEGER, v TEXT)"),
            ("index", "ix_a_v", "a", "CREATE INDEX ix_a_v ON a (v)"),
            ("table", "app_users", "app_users", "CREATE TABLE app_users (id INTEGER)"),
            ("index", "idx_app_users_name", "app_users", "CREATE INDEX idx_app_users_name ON app_users (name)"),
        ]);

        Assert.Equal(["index ix_a_v", "table a"], inScope.Select(o => $"{o.Type} {o.Name}"));
        Assert.Equal(["table app_users", "index idx_app_users_name"], ignored);
    }

    [Fact]
    public void 正典のテーブルに付いた余分なオブジェクトは管轄に入り差として出る()
    {
        var (inScope, ignored) = SchemaSnapshot.SplitByJurisdiction(Canon,
        [
            ("table", "a", "a", "CREATE TABLE a (id INTEGER, v TEXT)"),
            ("index", "ix_a_v", "a", "CREATE INDEX ix_a_v ON a (v)"),
            ("trigger", "trg_rogue", "a", "CREATE TRIGGER trg_rogue BEFORE UPDATE ON a BEGIN SELECT 1; END"),
        ]);

        Assert.Empty(ignored);
        var diff = SchemaSnapshot.Diff(Canon, inScope, "正典", "稼働");
        var line = Assert.Single(diff);
        Assert.Equal("正典 に無い: trigger trg_rogue", line);
    }

    [Fact]
    public void 正典のテーブルが稼働に無ければ差として出る()
    {
        var (inScope, _) = SchemaSnapshot.SplitByJurisdiction(Canon,
        [
            ("index", "ix_a_v", "a", "CREATE INDEX ix_a_v ON a (v)"),
        ]);

        var diff = SchemaSnapshot.Diff(Canon, inScope, "正典", "稼働");
        Assert.Contains("稼働 に無い: table a", diff);
    }
}
