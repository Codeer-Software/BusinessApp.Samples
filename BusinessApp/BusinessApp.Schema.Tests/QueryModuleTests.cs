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
    /// 空値検索（電帳通達 8-13）の絞り込みのように、<b>候補の値と SQL の分岐が文字列でしか結ばれていない</b>
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

    /// <summary>
    /// <c>LIKE</c> のエスケープが、素通しになっていないこと。
    /// </summary>
    /// <remarks>
    /// <para><b>逃がす順序は「まず <c>\</c> を、次に <c>%</c> と <c>_</c> を」</b>で、
    /// 最初の 1 手を <c>replace(x, '\', '\')</c>（＝何もしない）と書き損じても
    /// <b>SQL は正しく走り、例外も出ない</b>。壊れるのは <c>\</c> を含む語で検索したときだけで、
    /// 利用者には「該当なし」としか見えない（2026-08-31 に新しい 2 本で実際にこう書き損じた。qa/03 L-20）。</para>
    /// <para><b>既存の検査では永久に捕まらない。</b> 宣言と SQL の突き合わせも列の突き合わせも、
    /// <b>検索欄を全部 NULL で流す</b>ので LIKE の枝に入らない。</para>
    /// <para><c>ESCAPE</c> を書いた回数だけ、<c>\</c> の二重化があることを見る。
    /// <b>静かに壊れるのはこの 1 手だけ</b>である——逃がす対象（<c>%</c> と <c>_</c>）を
    /// 書き損じた <c>replace(x, '%', '%')</c> はパターンを変えないので、素直に「絞れない」で現れる。</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(QueryModules))]
    public void LIKE_のエスケープが素通しになっていない(string relativePath)
    {
        var module = Load(relativePath);

        Assert.Equal(
            Count(module.Sql, @"ESCAPE '\\'"),
            Count(module.Sql, @"'\\', '\\\\'"));
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

    /// <summary>
    /// 並び順に<b>外部キー</b>を使っていないこと。
    /// </summary>
    /// <remarks>
    /// <para><c>fiscal_years.id</c> のような代理キーは <c>AUTOINCREMENT</c> の挿入順でしかなく、
    /// <b>年代とは無関係</b>である。第 17 期のデモデータを後から入れれば id は第 18 期より大きくなり、
    /// <b>元帳が新しい年度から先に出る</b>——しかも列は並べ替え不可なので利用者は戻せない
    /// （2026-08-30 に実際にそう書いた。qa/03 L-19）。順序は「順番に意味がある列」
    /// （開始日・日付・コード）で表す。</para>
    /// <para><b>見るのは <c>_id</c> で終わる列だけ</b>である。裸の <c>id</c> は
    /// <b>同着の解き方</b>として正しく使える——下書きは伝票番号を持たないので、
    /// 同じ計上日・同じ入力年月日だと並びが一意に決まらず、ページ送りで行が重複したり欠けたりする
    /// （qa/02 R29-08 で <c>JournalEntryList</c> の <c>ORDER BY</c> 末尾に足した）。
    /// <b>禁じているのは順序の「意味」を代理キーに持たせること</b>で、同着の解消は別の話である。</para>
    /// <para><c>PARTITION BY</c> は対象外。あちらは<b>同一性</b>で束ねるので、識別子が正しい。</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(QueryModules))]
    public void 並び順に外部キーを使っていない(string relativePath)
    {
        var module = Load(relativePath);

        Assert.All(ForeignKeysInOrderBy(module.Sql), column =>
            Assert.Fail($"ORDER BY に {column} がある。代理キーは順序に使えない（qa/03 L-19）"));
    }

    /// <summary>この検査が「1 本も ORDER BY を見つけられず素通り」で緑にならないための土台。</summary>
    /// <remarks>帳簿は必ず並び順を決めている。0 本なら読み方が壊れている（qa/03 L-15）。</remarks>
    [Fact]
    public void 並び順を実際に読めている()
        => Assert.NotEmpty(Discover().SelectMany(path => OrderByClauses(new QueryModule(path).Sql)));

    /// <summary>
    /// 壊した SQL を食わせて、実際に鳴ることを確かめる。
    /// </summary>
    /// <remarks>
    /// <b>「違反 0 件」を表明するだけのテストは、読み方が壊れても緑になる</b>（qa/03 L-15）。
    /// とくに <c>date(...)</c> の閉じ括弧で節を切ってしまう読み方は、
    /// <b>1 項目めだけを見て残りを見逃す</b>——実データがまさにその形である。
    /// </remarks>
    [Theory]
    // 素直な違反。
    [InlineData("select 1 order by e.fiscal_year_id", "fiscal_year_id")]
    // **関数の後ろに隠れた違反。** 「次の `)` まで」と読むと見逃す。
    [InlineData("select 1 order by date(fy.start_date), e.fiscal_year_id", "fiscal_year_id")]
    // **窓関数の中の違反**（qa/03 L-19 の事故そのもの）。
    [InlineData("select sum(x) over (partition by a order by e.fiscal_year_id) from t", "fiscal_year_id")]
    // **注釈に括弧を書いた後ろに隠れた違反。** コメントを落とさないと、その `)` で節が切れる。
    [InlineData("select 1 order by a.code, -- 電帳通達 8-14 (注)\n         e.fiscal_year_id", "fiscal_year_id")]
    // **注釈に `LIMIT` の語を書いた後ろ。** 節を終わらせるキーワードと読むと切れる。
    [InlineData("select 1 order by a.code, -- LIMIT は付けない\n         e.fiscal_year_id", "fiscal_year_id")]
    public void 順序に使えない代理キーを見つける(string sql, string expected)
        => Assert.Contains(expected, ForeignKeysInOrderBy(sql), StringComparer.Ordinal);

    /// <summary>正しい書き方では鳴らない（鳴りっぱなしの関門は、赤を無視させる）。</summary>
    [Theory]
    // 裸の id は**同着の解き方**として正しい（qa/02 R29-08）。
    [InlineData("select 1 order by date(e.posting_date) desc, e.id desc")]
    // `PARTITION BY` は**同一性**で束ねるので識別子が正しい。
    [InlineData("select sum(x) over (partition by e.fiscal_year_id order by e.entry_no) from t")]
    // 並びの外にある `_id` は関係ない。
    [InlineData("select l.account_id from t where l.account_id = @a order by a.code")]
    public void 正しい並び順では鳴らない(string sql)
        => Assert.Empty(ForeignKeysInOrderBy(sql));

    /// <summary><c>ORDER BY</c> 節に現れる、<c>_id</c> で終わる列。</summary>
    internal static IReadOnlyList<string> ForeignKeysInOrderBy(string sql)
        => [.. OrderByClauses(sql)
            .SelectMany(clause => Regex.Matches(clause, @"\b(?:\w+\.)?(\w+_id)\b")
                .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// <c>ORDER BY</c> から、その節の終わりまで。
    /// </summary>
    /// <remarks>
    /// <para>窓関数の中（<c>OVER (PARTITION BY ... ORDER BY ...)</c>）も対象である——
    /// <b>累計の並びを代理キーにしたのが qa/03 L-19 の事故そのもの</b>だった。</para>
    /// <para><b>括弧の深さを数える。</b> 単純に「次の <c>)</c> まで」で切ると、
    /// <c>ORDER BY date(fy.start_date), e.entry_no</c> が <c>date(</c> の閉じ括弧で切れて、
    /// <b>2 項目め以降を一度も見ない</b>。節の終わりは、外側の括弧が閉じたところか、
    /// 次のキーワード（<c>LIMIT</c> / <c>OFFSET</c> / <c>ROWS</c>）か、文の終わりである。
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<string> OrderByClauses(string sql)
    {
        var clauses = new List<string>();

        // **コメントを落としてから走る**（2026-09-02 の自己レビュー）。このリポジトリの SQL は
        // `-- 電帳通達 8-14 (注)` のように注釈へ括弧を普通に書いており、落とさないと
        // その `)` で節が切れて**2 項目め以降を永久に見なくなる**。
        sql = WithoutSqlComments(sql ?? string.Empty);

        foreach (Match start in Regex.Matches(sql, @"ORDER\s+BY\s+", RegexOptions.IgnoreCase))
        {
            var depth = 0;
            var index = start.Index + start.Length;
            var from = index;

            while (index < sql.Length)
            {
                var c = sql[index];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')' && depth-- == 0)
                {
                    break;
                }
                else if (depth == 0 && (c == ';' || IsClauseKeywordAt(sql, index)))
                {
                    break;
                }

                index++;
            }

            clauses.Add(sql[from..index]);
        }

        return clauses;
    }

    /// <summary>
    /// 行コメント（<c>--</c> から行末）を、<b>同じ長さの空白</b>に置き換える。
    /// </summary>
    /// <remarks>
    /// 消さずに空白にするのは、<b>切り出した節の中身を元の並びのまま読むため</b>である。
    /// <b>文字列リテラルの中の <c>--</c> は落とさない</b>——SQL の値そのものが消える。
    /// </remarks>
    internal static string WithoutSqlComments(string sql)
        => Regex.Replace(
            sql,
            @"'[^'\n]*'|--[^\n]*",
            match => match.Value.StartsWith("--", StringComparison.Ordinal)
                ? new string(' ', match.Value.Length)
                : match.Value);

    /// <summary><c>ORDER BY</c> 節を終わらせるキーワードが、この位置から始まっているか。</summary>
    private static bool IsClauseKeywordAt(string sql, int index)
        => IsWordBoundary(sql, index - 1)
           && ClauseKeywords.Any(keyword =>
               sql.Length - index >= keyword.Length
               && sql.AsSpan(index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)
               && IsWordBoundary(sql, index + keyword.Length));

    private static bool IsWordBoundary(string sql, int index)
        => index < 0 || index >= sql.Length || (!char.IsLetterOrDigit(sql[index]) && sql[index] != '_');

    private static readonly string[] ClauseKeywords = ["LIMIT", "OFFSET", "ROWS", "RANGE"];

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
