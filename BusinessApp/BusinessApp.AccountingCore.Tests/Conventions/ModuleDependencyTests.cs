namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Text.RegularExpressions;

/// <summary>
/// モジュール間の依存方向（ADR-0013）。
/// </summary>
/// <remarks>
/// <para>会計は本質的に相互参照が多いので「モジュール間の依存ゼロ」は成立しない。
/// 目指すのは<b>依存の向きを一方通行に固定する</b>ことである。</para>
/// <para>検査の仕方: ソースファイルの中に <c>BusinessApp.AccountingCore.&lt;他モジュール&gt;</c> という
/// 文字列が現れたら、それは他モジュールへの参照とみなす。<c>using</c>・<c>using static</c>・
/// エイリアス・完全修飾のすべてがこの 1 つの規則で捕まる。IL 解析も外部ライブラリも要らない。</para>
/// <para>副作用として、XML ドキュメントコメントで他モジュールの型を完全修飾しても違反になる。
/// これは望ましい副作用として受け入れる（型名だけで書けばよい）。</para>
/// </remarks>
public class ModuleDependencyTests
{
    /// <summary>
    /// モジュールと、依存してよい相手。ここに無いモジュールを作ると検査が落ちるので、
    /// <b>フォルダを増やしたら必ずこの表を更新することになる</b>。
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedDependencies = new(StringComparer.Ordinal)
    {
        // 共有カーネル。どのモジュールにも依存しない。
        ["Shared"] = [],

        // 制度の分類。勘定科目を知らない（税区分は科目に依存しない）。
        ["ConsumptionTax"] = ["Shared"],

        // マスタ層。勘定科目は「既定税区分」を持つので、向きは Accounts → ConsumptionTax。
        // 逆向き（税区分が科目を知る）を許可しないために、この向きを明示しておく。
        ["Accounts"] = ["Shared"],
        ["Periods"] = ["Shared"],

        // 記帳。マスタと制度の上に載る。
        ["Journals"] = ["Shared", "Accounts", "Periods", "ConsumptionTax"],
    };

    [Fact]
    public void モジュールは許可された相手にしか依存しない()
    {
        var violations = new List<string>();

        foreach (var (module, file, referenced) in ModuleReferences())
        {
            if (!AllowedDependencies.TryGetValue(module, out var allowed))
            {
                continue;   // 表に無いモジュールは別のテストが報告する
            }

            if (!allowed.Contains(referenced, StringComparer.Ordinal))
            {
                violations.Add($"{file}: {module} は {referenced} に依存できない（許可: {string.Join(" / ", allowed.DefaultIfEmpty("なし"))}）");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Distinct()));
    }

    [Fact]
    public void すべてのモジュールが依存表に載っている()
    {
        var actual = Directory.EnumerateDirectories(ProjectPaths.SourceProject)
            .Select(Path.GetFileName)
            .Where(name => name is not ("bin" or "obj"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var declared = AllowedDependencies.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, actual);
    }

    [Fact]
    public void 依存表に循環がない()
    {
        foreach (var module in AllowedDependencies.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            Assert.False(ReachesItself(module, module, visited), $"{module} から自分自身へ戻る依存がある");
        }
    }

    [Fact]
    public void 依存表の相手はすべて実在するモジュールである()
    {
        var unknown = AllowedDependencies
            .SelectMany(entry => entry.Value.Select(target => (entry.Key, target)))
            .Where(pair => !AllowedDependencies.ContainsKey(pair.target))
            .Select(pair => $"{pair.Key} → {pair.target}（そんなモジュールはない）");

        Assert.Empty(unknown);
    }

    /// <summary>
    /// 純粋ドメインの依存ゼロ（ADR-0008）を、規約ではなくファイルの内容で確かめる。
    /// WASM に載るのでサイズと起動時間に直接効く。
    /// </summary>
    [Fact]
    public void 純粋ドメインは外部パッケージに依存しない()
    {
        var csproj = File.ReadAllText(
            Path.Combine(ProjectPaths.SourceProject, $"{ProjectPaths.SourceProjectName}.csproj"));

        Assert.DoesNotContain("<PackageReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);
    }

    private bool ReachesItself(string origin, string current, HashSet<string> visited)
    {
        foreach (var next in AllowedDependencies.GetValueOrDefault(current, []))
        {
            if (next == origin)
            {
                return true;
            }

            if (visited.Add(next) && ReachesItself(origin, next, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<(string Module, string File, string Referenced)> ModuleReferences()
    {
        var pattern = new Regex(Regex.Escape(ProjectPaths.RootNamespace) + @"\.(\w+)");

        foreach (var path in ProjectPaths.SourceFiles(ProjectPaths.SourceProject))
        {
            var relative = Path.GetRelativePath(ProjectPaths.SourceProject, path);
            var module = relative.Split(Path.DirectorySeparatorChar)[0];
            var text = File.ReadAllText(path);

            foreach (Match match in pattern.Matches(text))
            {
                var referenced = match.Groups[1].Value;
                if (referenced != module)
                {
                    yield return (module, relative, referenced);
                }
            }
        }
    }
}
