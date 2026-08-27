namespace BusinessApp.Partners.Tests.Conventions;

using System.Xml.Linq;

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
    /// サーバ層が会計コアを参照していない。
    /// </summary>
    /// <remarks>
    /// 純粋層と違い、サーバ層は CLB と共有インフラに依存してよい。
    /// <b>依存してはいけないのは会計コアである。</b> 参照が 1 本入った瞬間に
    /// 「取引先だけのデプロイ」が成立しなくなるが、ビルドは緑のまま通る。
    /// </remarks>
    [Fact]
    public void サーバ層は会計コアを参照しない()
    {
        var directory = Path.Combine(
            Path.GetDirectoryName(TestLayoutConvention.FindProjectDirectory())!, "BusinessApp.Partners.Server");
        var csproj = File.ReadAllText(Path.Combine(directory, "BusinessApp.Partners.Server.csproj"));

        var accountingReferences = XDocument.Parse(csproj)
            .Descendants("ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")?.Value ?? string.Empty))
            .Where(name => name.StartsWith("BusinessApp.AccountingCore", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(accountingReferences);
    }

    /// <summary>ソースを 1 本も見つけられていない状態で緑にならないための土台。</summary>
    [Fact]
    public void 純粋層のソースを見つけられる()
    {
        Assert.True(Directory.Exists(SourceProject), "BusinessApp.Partners が見つからない");
        Assert.NotEmpty(Directory.EnumerateFiles(SourceProject, "*.cs", SearchOption.TopDirectoryOnly));
    }
}
