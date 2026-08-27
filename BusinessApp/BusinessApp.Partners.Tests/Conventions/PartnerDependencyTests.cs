namespace BusinessApp.Partners.Tests.Conventions;

using System.Xml.Linq;

using BusinessApp.Partners.Server;
using BusinessApp.TestSupport;

/// <summary>
/// 取引先部品の純粋層が<b>何にも依存しない</b>こと（ADR-0024 §4・ADR-0025 §2）。
/// </summary>
/// <remarks>
/// <para><b>これが部品の分割の芯である。</b> 取引先は会計コアより上位の実体で、
/// 「取引先アプリと取引先ライブラリだけ用意すれば使える」形が成立するかは、
/// ここが空であることに懸かっている。逆流（取引先 → 会計コア）は
/// <b>参照を持たないことでコンパイラが物理的に禁じる</b>——文字列照合の規約より強い。</para>
/// <para>会計コア側から見た向き（会計コア → 取引先の 1 本だけ）は
/// <c>AccountingCore.Tests</c> の <c>ModuleDependencyTests</c> が検査する。
/// <b>両側から見ないと、片方だけ緩めたときに気づけない。</b></para>
/// <para>csproj のテキストと<b>ビルド後のアセンブリ</b>の両方を見る。
/// テキストだけだと Directory.Build.props と生アセンブリ参照が素通りし、
/// アセンブリだけだと「参照はしたが使っていない」宣言が残る。</para>
/// </remarks>
public class PartnerDependencyTests
{
    private static readonly string SourceProject = Path.Combine(
        Path.GetDirectoryName(TestLayoutConvention.FindProjectDirectory())!, "BusinessApp.Partners");

    [Fact]
    public void 純粋層は外部アセンブリに依存しない()
    {
        var referenced = typeof(PartnerId).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal)
                           && name is not ("netstandard" or "mscorlib"))
            .ToList();

        Assert.Empty(referenced);
    }

    [Fact]
    public void 純粋層は参照も暗黙usingも持たない()
    {
        var csproj = File.ReadAllText(Path.Combine(SourceProject, "BusinessApp.Partners.csproj"));

        Assert.DoesNotContain("<PackageReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<Using ", csproj, StringComparison.Ordinal);
    }

    /// <summary>
    /// 取引先部品のプロジェクトが参照してよい、リポジトリ内のプロジェクト。
    /// </summary>
    /// <remarks>
    /// <b>許可リストにする。</b> 「会計コアを参照しない」という否定リストだと、
    /// 会計側が別名のプロジェクトに割れた瞬間に無言で効かなくなる
    /// （2026-08-27 の自己レビュー R16-05）。CLB と NuGet は名前で除く。
    /// </remarks>
    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.Ordinal)
    {
        ["BusinessApp.Partners"] = [],
        ["BusinessApp.Partners.Server"] = ["BusinessApp.Partners", "BusinessApp.ServerSupport"],
        // 純粋層のテストがサーバ層を参照しているのは、**この検査がサーバ層のアセンブリを
        // 見るためだけ**である（下の「サーバ層は会計コアのアセンブリに依存しない」）。
        // 検査以外でサーバ層の型を使わない。
        ["BusinessApp.Partners.Tests"] =
            ["BusinessApp.Partners", "BusinessApp.Partners.Server", "BusinessApp.TestSupport"],
        ["BusinessApp.Partners.Server.Tests"] = ["BusinessApp.Partners.Server", "BusinessApp.TestSupport"],
    };

    /// <summary>
    /// 取引先部品のどのプロジェクトも、許可された相手しか参照しない。
    /// </summary>
    /// <remarks>
    /// <para>純粋層と違い、サーバ層は CLB と共有インフラに依存してよい。
    /// <b>依存してはいけないのは会計コアである。</b> 参照が 1 本入った瞬間に
    /// 「取引先だけのデプロイ」が成立しなくなるが、ビルドは緑のまま通る。</para>
    /// <para><b>テストプロジェクトも見る。</b> 「依存の逆流はコードだけでなくテストでも作らない」
    /// と <c>PartnerServer</c> のコメントが宣言しているのに、機械が守っていなかった
    /// （2026-08-27 の自己レビュー R16-06）。</para>
    /// </remarks>
    [Fact]
    public void 取引先部品は許可された相手しか参照しない()
    {
        var root = Path.GetDirectoryName(TestLayoutConvention.FindProjectDirectory())!;
        var violations = new List<string>();

        foreach (var (project, allowed) in AllowedReferences)
        {
            var csproj = Path.Combine(root, project, project + ".csproj");
            Assert.True(File.Exists(csproj), $"{project} の csproj が見つからない");

            var actual = XDocument.Parse(File.ReadAllText(csproj))
                .Descendants("ProjectReference")
                .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")?.Value ?? string.Empty))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var unexpected = actual.Except(allowed, StringComparer.Ordinal).ToList();
            if (unexpected.Count > 0)
            {
                violations.Add($"{project}: {string.Join(" / ", unexpected)} を参照している"
                             + $"（許可: {string.Join(" / ", allowed.DefaultIfEmpty("なし"))}）");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// サーバ層を<b>ビルド後のアセンブリ</b>でも確かめる。
    /// </summary>
    /// <remarks>
    /// csproj のテキストだけだと <c>Directory.Build.props</c> と生アセンブリ参照が素通りする。
    /// 純粋層は同じことを <see cref="純粋層は外部アセンブリに依存しない"/> が見ているが、
    /// サーバ層には無かった（2026-08-27 の自己レビュー R16-05）。
    /// </remarks>
    [Fact]
    public void サーバ層は会計コアのアセンブリに依存しない()
    {
        var referenced = typeof(PartnerSubmitGate).Assembly
            .GetReferencedAssemblies()
            .Select(assembly => assembly.Name!)
            .Where(name => name.StartsWith("BusinessApp.", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["BusinessApp.Partners", "BusinessApp.ServerSupport"], referenced);
    }

    /// <summary>ソースを 1 本も見つけられていない状態で緑にならないための土台。</summary>
    [Fact]
    public void 純粋層のソースを見つけられる()
    {
        Assert.True(Directory.Exists(SourceProject), "BusinessApp.Partners が見つからない");
        Assert.NotEmpty(Directory.EnumerateFiles(SourceProject, "*.cs", SearchOption.TopDirectoryOnly));
    }
}
