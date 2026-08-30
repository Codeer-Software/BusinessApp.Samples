namespace BusinessApp.Schema.Tests;

using System.Text.Json;
using System.Text.RegularExpressions;
using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;

/// <summary>
/// クエリモジュール（帳簿・集計）の SQL と宣言が食い違っていないこと。
/// </summary>
/// <remarks>
/// <para>クエリモジュールは <b>SQL が別ファイル</b>にあり、出力列と入力パラメータを
/// <c>QuerySetting.Parameters</c> に<b>手で宣言する</b>（<c>_specs/QueryAndSql.md</c>）。
/// この 2 つがずれても <c>designcheck</c> は緑のままで、<b>実機で画面を開くまで分からない</b>。
/// 宣言し忘れた列は黙って消え、余分に宣言したパラメータは実行時に落ちる。</para>
/// <para>だから<b>本物の DDL に本物の SQL を流して</b>突き合わせる。
/// 帳簿は今後増えるので、モジュールを列挙して全部にかける。</para>
/// </remarks>
public class QueryModuleTests
{
    /// <summary>システムが束縛する予約パラメータ。宣言しないのが正しい。</summary>
    private static readonly string[] Reserved = ["rows_per_page", "offset", "current_user_id"];

    public static TheoryData<string> QueryModules
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in Discover())
            {
                data.Add(Path.GetRelativePath(TestDatabase.ModulesDirectory, path));
            }

            return data;
        }
    }

    [Fact]
    public void クエリモジュールが一つ以上ある()
    {
        // 列挙が空回りしていると、以下のテストが「全部通った」ように見えてしまう。
        Assert.NotEmpty(Discover());
    }

    [Theory]
    [MemberData(nameof(QueryModules))]
    public void SQL_が同じ名前で隣に置いてある(string relativePath)
    {
        var module = Load(relativePath);

        Assert.True(File.Exists(module.SqlPath), $"SQL が無い: {module.SqlPath}");
        Assert.NotEmpty(module.Sql.Trim());
    }

    [Theory]
    [MemberData(nameof(QueryModules))]
    public void 宣言した入力パラメータと_SQL_の使用が一致する(string relativePath)
    {
        var module = Load(relativePath);

        // 宣言だけあって使われない → 実行時に「パラメータが多い」で落ちる。
        // 使われているのに宣言が無い → 束縛されず、検索欄が効かない（静かな失敗）。
        Assert.Equal(module.DeclaredParameters, module.UsedParameters);
    }

    [Theory]
    [MemberData(nameof(QueryModules))]
    public void SQL_が返す列と宣言した出力列が一致する(string relativePath)
    {
        var module = Load(relativePath);

        using var db = SchemaSeed.CreateWithPostedEntry();

        Assert.Equal(module.DeclaredColumns, ReturnedColumns(db, module));
    }

    [Theory]
    [MemberData(nameof(QueryModules))]
    public void フィールドの列名がすべて宣言されている(string relativePath)
    {
        var module = Load(relativePath);

        // `DbColumn` が宣言に無いフィールドは、画面に置いても値が入らない。
        var declared = module.DeclaredColumns.Concat(module.DeclaredParameters).ToHashSet(StringComparer.Ordinal);

        Assert.All(module.FieldColumns, column =>
            Assert.True(declared.Contains(column), $"{column} が QuerySetting.Parameters に無い"));
    }

    /// <summary>
    /// 選択肢の値が、SQL の分岐にそのまま現れること。
    /// </summary>
    /// <remarks>
    /// 空値検索（通達 8-13）の絞り込みのように、<b>候補の値と SQL の分岐が文字列でしか結ばれていない</b>
    /// ものがある。どちらかを直すとどの分岐にも当たらず、<b>例外にならずに 0 件が返る</b>。
    /// 画面には「該当なし」としか出ないので、機能が死んだことに誰も気づけない。
    /// </remarks>
    [Theory]
    [MemberData(nameof(QueryModules))]
    public void 選択肢の値が_SQL_の分岐に現れる(string relativePath)
    {
        var module = Load(relativePath);

        Assert.All(module.CandidateValues, value =>
            Assert.True(
                module.Sql.Contains($"'{value}'", StringComparison.Ordinal),
                $"候補の値 {value} が SQL に無い（分岐と候補がずれている）"));
    }

    [Theory]
    [MemberData(nameof(QueryModules))]
    public void 実テーブルを持たず読み取り専用である(string relativePath)
    {
        var module = Load(relativePath);

        // `DbTable` が空であることが「書き込み経路が無い」ことの表明である。
        Assert.Equal(string.Empty, module.Root.GetProperty("DbTable").GetString());
        Assert.False(module.Root.GetProperty("CanCreate").GetBoolean());
        Assert.False(module.Root.GetProperty("CanUpdate").GetBoolean());
        Assert.False(module.Root.GetProperty("CanDelete").GetBoolean());
    }

    /// <summary>SQL を実行して、返ってきた列名を順序どおりに読む。</summary>
    private static IReadOnlyList<string> ReturnedColumns(SqliteConnection db, QueryModule module)
    {
        using var command = db.CreateCommand();
        command.CommandText = module.Sql;
        foreach (var name in module.UsedParameters)
        {
            // 検索欄が空のとき CLB は NULL を束縛する。**その状態で流れることも検査になる**
            // （条件の書き方を間違えると、無条件のはずが 0 件になる）。
            command.Parameters.AddWithValue("@" + name, DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        return [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
    }

    private static IReadOnlyList<string> Discover()
        => [.. Directory.GetFiles(TestDatabase.ModulesDirectory, "*.mod.json", SearchOption.AllDirectories)
            .Where(HasQueryField)
            .OrderBy(p => p, StringComparer.Ordinal)];

    private static bool HasQueryField(string path)
        => File.ReadAllText(path).Contains("QueryFieldDesign", StringComparison.Ordinal);

    private static QueryModule Load(string relativePath)
        => new(Path.Combine(TestDatabase.ModulesDirectory, relativePath));

    private sealed class QueryModule
    {
        public QueryModule(string path)
        {
            Root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;

            var query = Root.GetProperty("Fields").EnumerateArray().Single(
                f => f.GetProperty("TypeFullName").GetString()!.EndsWith("QueryFieldDesign", StringComparison.Ordinal));
            var parameters = query.GetProperty("QuerySetting").GetProperty("Parameters").EnumerateArray().ToList();

            DeclaredParameters = [.. parameters
                .Where(p => p.GetProperty("IsParameter").GetBoolean())
                .Select(p => p.GetProperty("Name").GetString()!)
                .OrderBy(n => n, StringComparer.Ordinal)];
            DeclaredColumns = [.. parameters
                .Where(p => !p.GetProperty("IsParameter").GetBoolean())
                .Select(p => p.GetProperty("Name").GetString()!)];

            // 候補は「表示テキスト,値」の形。**値だけ**が SQL と結ばれる。
            CandidateValues = [.. Root.GetProperty("Fields").EnumerateArray()
                .Where(f => f.TryGetProperty("Candidates", out var c) && c.GetArrayLength() > 0)
                .SelectMany(f => f.GetProperty("Candidates").EnumerateArray())
                .Select(c => c.GetString()!.Split(',')[^1])];

            FieldColumns = [.. Root.GetProperty("Fields").EnumerateArray()
                .Where(f => f.TryGetProperty("DbColumn", out var c) && !string.IsNullOrEmpty(c.GetString()))
                .Select(f => f.GetProperty("DbColumn").GetString()!)];

            var name = Path.GetFileName(path)[..^".mod.json".Length];
            SqlPath = Path.Combine(
                Path.GetDirectoryName(path)!, $"{name}.{query.GetProperty("Name").GetString()}.sql");
            Sql = File.Exists(SqlPath) ? File.ReadAllText(SqlPath) : string.Empty;

            UsedParameters = [.. Regex.Matches(Sql, @"@(\w+)")
                .Select(m => m.Groups[1].Value)
                .Where(n => !Reserved.Contains(n, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal)];
        }

        public JsonElement Root { get; }

        public string SqlPath { get; }

        public string Sql { get; }

        /// <summary>入力パラメータ（`IsParameter: true`）。順序を無視するため名前順に並べてある。</summary>
        public IReadOnlyList<string> DeclaredParameters { get; }

        /// <summary>
        /// 出力列（`IsParameter: false`）。<b>SELECT と順序込みで突き合わせる。</b>
        /// </summary>
        /// <remarks>
        /// <b>CLB が順序を要求しているという根拠は無い</b>（`_specs/QueryAndSql.md` が言うのは
        /// 「`Name` をフィールドの `DbColumn` と一致させる」だけで、並びには触れていない）。
        /// ここで順序も見ているのは<b>本プロジェクトの規約</b>で、
        /// 列を足したときに宣言と SQL の片方だけを直したことに気づくための当たり判定である。
        /// 緩めてよいかを判断するときは、この違いを踏まえること。
        /// </remarks>
        public IReadOnlyList<string> DeclaredColumns { get; }

        /// <summary>Select の候補が持つ値（「表示テキスト,値」の値のほう）。</summary>
        public IReadOnlyList<string> CandidateValues { get; }

        public IReadOnlyList<string> FieldColumns { get; }

        public IReadOnlyList<string> UsedParameters { get; }
    }
}
