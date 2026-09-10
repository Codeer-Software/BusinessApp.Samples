namespace BusinessApp.AccountingCore.Server.Tests.Conventions;

using System.Text.RegularExpressions;

using BusinessApp.TestSupport;

/// <summary>
/// 層の向き（ADR-0050 の決定 4）。<b>Presentation → Application → Infrastructure → ドメイン</b>。
/// </summary>
/// <remarks>
/// <para><b>軽く守る。</b> 反転（インターフェース）は要求しない。組み立てのために Presentation が Infrastructure を
/// 参照するのは許す。禁じるのは 2 つだけ——<b>Infrastructure が Application・Presentation を参照する</b>ことと、
/// <b>Application が Presentation を参照する</b>こと。</para>
/// <para><b>名前空間の字面で見る</b>（ADR-0013 の決定 4 と同じ手法。外部ライブラリを使わない）。
/// `using` でも完全修飾でも当たる。コメントの中の言及も当たる——<b>「呼ぶ」と書いたら呼んでいる</b>と扱う
/// （書き分けたいなら、層の名前を出さずに書く）。</para>
/// <para><b>層のフォルダ名は 3 つに限る。</b> 機能フォルダの直下に置けるのは `Application`・`Infrastructure`・`Presentation` だけで、
/// 層の無いファイル（`Journals/X.cs`）は赤にする——「まだ分けていない」が黙って残らないように。</para>
/// </remarks>
public class LayerDependencyTests
{
    private static readonly string SourceProject = Path.Combine(
        Path.GetDirectoryName(TestLayoutConvention.FindProjectDirectory())!,
        "BusinessApp.AccountingCore.Server");

    private static readonly string[] Layers = ["Application", "Infrastructure", "Presentation"];

    /// <summary>層 → 参照してはいけない層。</summary>
    private static readonly Dictionary<string, string[]> Forbidden = new(StringComparer.Ordinal)
    {
        ["Infrastructure"] = ["Application", "Presentation"],
        ["Application"] = ["Presentation"],
        ["Presentation"] = [],
    };

    [Fact]
    public void すべてのソースは機能フォルダの下の層フォルダにある()
    {
        var misplaced = SourceFiles()
            .Select(path => Path.GetRelativePath(SourceProject, path))
            .Where(relative =>
            {
                var segments = relative.Split(Path.DirectorySeparatorChar);
                return segments.Length != 3 || !Layers.Contains(segments[1], StringComparer.Ordinal);
            })
            .ToList();

        Assert.True(misplaced.Count == 0,
            "機能/層/ファイル の形に置く（層は Application・Infrastructure・Presentation）:" + Environment.NewLine
            + string.Join(Environment.NewLine, misplaced));
    }

    [Fact]
    public void 層の向きが逆流していない()
    {
        var violations = new List<string>();

        foreach (var path in SourceFiles())
        {
            var relative = Path.GetRelativePath(SourceProject, path);
            var segments = relative.Split(Path.DirectorySeparatorChar);
            if (segments.Length != 3)
            {
                continue;   // 置き場の誤りは上のテストが言う
            }

            var layer = segments[1];
            var text = File.ReadAllText(path);
            foreach (var forbidden in Forbidden.TryGetValue(layer, out var list) ? list : [])
            {
                // `BusinessApp.AccountingCore.Server.<機能>.<層>` の字面。機能は問わない。
                var pattern = new Regex(@"\bBusinessApp\.AccountingCore\.Server\.\w+\." + forbidden + @"\b");
                if (pattern.IsMatch(text))
                {
                    violations.Add($"{relative}: {layer} は {forbidden} を参照できない（ADR-0050 の決定 4）");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void 検査しているファイルが空でない()
        => Assert.True(SourceFiles().Count() > 20, "ソースを集められていない。");

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(SourceProject, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(SourceProject, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));
}
