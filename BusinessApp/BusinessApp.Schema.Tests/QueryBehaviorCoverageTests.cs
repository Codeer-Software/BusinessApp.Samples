namespace BusinessApp.Schema.Tests;

using System.Reflection;

using BusinessApp.TestSupport;

/// <summary>
/// <b>クエリモジュールを足したら、行を入れて読み戻すテストも足す。</b>
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は<b>全モジュールに自動でかかる</b>が、
/// 見ているのは<b>宣言と SQL の整合</b>だけである。
/// <c>COALESCE</c> の段の取り違えも、結合の向きも、並び順も、
/// <b>行を入れて読み戻さないと分からない</b>（qa/03 の L-12・L-19・L-20）。</para>
/// <para><b>その行動テストは帳簿ごとの手書きで、書き忘れを止める仕組みが無かった</b>
/// （<c>docs/qa/05_観点網羅の計器.md</c> §6。判断は ADR-0055）。
/// <b>実際に 1 本抜けていた</b>——<c>PartnerRegistrationList</c> は
/// 2026-08-31 に入ってから 2 週間、宣言の整合しか見られていなかった（qa/02 のラウンド 91）。</para>
/// <para><b>測れるのは「クラスがあるか」までである。</b>
/// 中身が薄いテストは見えない——<c>InvariantTraceabilityConvention</c> と同じ限界で、
/// <b>測れないものを測ったふりをしない</b>。それでも<b>「1 本も無い」は機械が言える。</b></para>
/// <para><b>判定は入力を受け取って結果を返す形に切り出してある</b>（<see cref="Missing"/>）。
/// ディスクと反射を見るのはその外側だけで、<b>そうしないと中身を空にしても緑のまま</b>になる
/// （<c>CSharpStyleConvention</c> が明記して避けている形。qa/02 の R8-11・R90-01）。</para>
/// </remarks>
public class QueryBehaviorCoverageTests
{
    /// <summary>行動テストのクラス名の形。<c>JournalBook</c> なら <c>JournalBookQueryTests</c>。</summary>
    /// <remarks>
    /// <b>対応表を持たない。</b> 表を作るとモジュールを足した人が表も直さねばならず、
    /// <b>直さなければ黙って外れる</b>（ADR-0012 §4 の「許容リストは必ず腐る」）。
    /// <b>名前の規約なら、規約から外れたクラスは最初から数えられない。</b>
    /// </remarks>
    private const string Suffix = "QueryTests";

    /// <summary>
    /// 行動テストを持つべきクエリモジュールの数の下限。<b>ラチェットである。</b>
    /// </summary>
    /// <remarks>
    /// <b>母数そのものに下限を置く。</b> 列挙が壊れて 0 件になると、
    /// <b>下の検査は「全部通った」ように見える</b>。
    /// </remarks>
    private const int MinimumQueryModules = 4;

    public static TheoryData<string> QueryModules
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Discover())
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Fact]
    public void クエリモジュールを数え落としていない()
    {
        var found = Discover();

        Assert.True(
            found.Count >= MinimumQueryModules,
            $"クエリモジュールが {found.Count} 本しか見つからない（下限 {MinimumQueryModules}）。"
            + $"探した先は {TestDatabase.ModulesDirectory} である（{string.Join(" / ", found)}）。");
    }

    [Theory]
    [MemberData(nameof(QueryModules))]
    public void 行を入れて読み戻すテストがある(string moduleName)
    {
        var missing = Missing([moduleName], BehaviorTests());

        Assert.True(
            missing.Count == 0,
            $"{moduleName} に対応する行動テストのクラス {moduleName}{Suffix} が"
            + "（あるいはその中の検査が）無い。"
            + "宣言と SQL の整合（QueryModuleTests）だけでは、結合の向きも並び順も検査されていない。");
    }

    /// <summary>
    /// <b>不足の数え方そのものを検体で固定する。</b>
    /// </summary>
    /// <remarks>
    /// <b>実物のリポジトリだけで確かめると、判定の中身を空にしても緑のままになる。</b>
    /// <b>ちょうど 1 本だけ足りない検体</b>を置く——0 件でも全件でもない形でなければ、
    /// 「何も返さない」実装と「全部返す」実装の両方を落とせない。
    /// </remarks>
    [Fact]
    public void 不足の数え方は検体で固定されている()
    {
        var tests = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["AlphaQueryTests"] = 3,
            ["BetaQueryTests"] = 1,
            // **クラスはあるが検査が 1 つも無い**——これは「ある」に数えない。
            ["GammaQueryTests"] = 0,
            // 名前の規約から外れたクラスは、どのモジュールにも結び付かない。
            ["DeltaTests"] = 5,
        };

        Assert.Equal(["Gamma"], Missing(["Alpha", "Beta", "Gamma"], tests));
        Assert.Empty(Missing(["Alpha", "Beta"], tests));
        Assert.Equal(["Delta", "Epsilon"], Missing(["Delta", "Epsilon"], tests));
    }

    /// <summary>
    /// <b>母数が <see cref="QueryModuleTests"/> と同じ集合である。</b>
    /// </summary>
    /// <remarks>
    /// <b>2 つの計器が別々の数え方をすると、片方から静かに漏れる。</b>
    /// CLB のサイドカーの名前は <c>&lt;モジュール名&gt;.&lt;クエリ項目の名前&gt;.sql</c> で、
    /// <b>項目の名前は <c>Query</c> に決まっていない</b>（`_specs/QueryAndSql.md`）——
    /// ファイル名の <c>.Query.sql</c> で数えると、
    /// <b>項目を別の名前にしたモジュールが書き忘れ検知から抜ける</b>。
    /// だから<b>こちらもモジュール定義（<c>*.mod.json</c>）の側から数える</b>。
    /// </remarks>
    [Fact]
    public void 母数はモジュール定義の側から数えている()
    {
        var byDefinition = Discover();
        var bySidecar = Directory
            .EnumerateFiles(TestDatabase.ModulesDirectory, "*.Query.sql", SearchOption.AllDirectories)
            .Select(path => Path.GetFileName(path)[..^".Query.sql".Length])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // いまはクエリ項目の名前が全部 `Query` なので両者は一致する。
        // **ずれた日に、ずれたことが分かる**ようにしてある（片方から黙って漏れない）。
        Assert.Equal(bySidecar, byDefinition);
    }

    /// <summary>
    /// <b>行動テストのクラスを 1 つも見つけられない、という空回りをしていない。</b>
    /// </summary>
    [Fact]
    public void 行動テストのクラスを見つけられている()
    {
        var tests = BehaviorTests();

        Assert.NotEmpty(tests);
        Assert.Contains(tests, entry => entry.Value > 0);
    }

    /// <summary>クエリモジュールの名前（<c>JournalBook</c> の形）。</summary>
    /// <remarks>
    /// <see cref="QueryModuleTests"/> と同じ数え方——<c>*.mod.json</c> のうち
    /// <c>QueryFieldDesign</c> を持つものを数える。
    /// <c>Designer/ClaudeCodeForDesigner/_samples/</c> の見本は
    /// <see cref="TestDatabase.ModulesDirectory"/> の外なので入らない。
    /// <b>あちらはデザイナが再生成する追跡外の生成物</b>で、数えると
    /// <b>テストが「手元に何が入っているか」に依存する</b>。
    /// </remarks>
    private static IReadOnlyList<string> Discover()
        => [.. Directory
            .EnumerateFiles(TestDatabase.ModulesDirectory, "*.mod.json", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("QueryFieldDesign", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path)[..^".mod.json".Length])
            .OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>
    /// カタログに載っているのに、行動テストが無いモジュール。<b>ディスクも反射も見ない。</b>
    /// </summary>
    /// <param name="modules">クエリモジュールの名前。</param>
    /// <param name="behaviorTests">クラス名 → そのクラスが持つ検査の本数。</param>
    public static IReadOnlyList<string> Missing(
        IEnumerable<string> modules, IReadOnlyDictionary<string, int> behaviorTests)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(behaviorTests);

        return [.. modules
            .Where(module => !behaviorTests.TryGetValue(module + Suffix, out var count) || count == 0)
            .OrderBy(module => module, StringComparer.Ordinal)];
    }

    /// <summary>
    /// このアセンブリの行動テストのクラスと、その検査の本数。
    /// </summary>
    /// <remarks>
    /// <para><b>ソースのファイル名ではなく型で見る。</b> ファイルなら、
    /// <b>プロジェクトから外れていても・中身が空でも置いてあるだけで通る</b>。</para>
    /// <para><b>見るのはこのアセンブリと、クラス自身が宣言した検査だけ</b>である
    /// （ADR-0055 決定 2）。行動テストを別のテストプロジェクトへ移したり、
    /// 共通の基底クラスへ <c>[Fact]</c> を括り出したりすると、
    /// <b>この検査は「無い」と言って赤くなる</b>——静かには壊れないが、
    /// <b>そう書いた人が理由を追えるようにここに書いておく</b>。
    /// 移すなら、この計器も一緒に動かすこと。</para>
    /// </remarks>
    private static IReadOnlyDictionary<string, int> BehaviorTests()
        => Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => type.IsClass && type.IsPublic)
            .Where(type => type.Name.EndsWith(Suffix, StringComparison.Ordinal))
            .ToDictionary(
                type => type.Name,
                type => type
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Count(method => method.GetCustomAttributes().Any(
                        attribute => attribute is FactAttribute or TheoryAttribute)),
                StringComparer.Ordinal);
}
