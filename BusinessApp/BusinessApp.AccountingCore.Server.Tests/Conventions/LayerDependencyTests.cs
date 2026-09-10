namespace BusinessApp.AccountingCore.Server.Tests.Conventions;

using System.Text.RegularExpressions;

using BusinessApp.TestSupport;

/// <summary>
/// 層の向き（ADR-0050 の決定 4）。<b>Presentation → Application → Infrastructure → ドメイン</b>。
/// </summary>
/// <remarks>
/// <para><b>軽く守る。</b> 反転（インターフェース）は要求しない。上の層が下のどの層を参照してもよい。
/// 禁じるのは 2 つだけ——<b>Infrastructure が Application・Presentation を参照する</b>ことと、
/// <b>Application が Presentation を参照する</b>こと。</para>
/// <para><b>層の名前が「名前空間として使われている」字面で見る</b>（ADR-0013 の決定 4 と同じ手法。外部ライブラリを使わない）。
/// 完全修飾（`BusinessApp.AccountingCore.Server.Journals.Presentation`）だけでなく、
/// <b>ファイルスコープ名前空間の内側では外側の名前空間の子を短く書ける</b>ので、
/// `using Presentation;`・`using Shared.Presentation;`・`Presentation.JournalAmendmentEndpoint.Create(...)` も当てる
/// （2026-09-10 の自己レビューで、最短の形がすり抜けていた。R67-01）。
/// コメントの中でも、層の名前の直後に `.` か `;` が続けば当てる——<b>「呼ぶ」と書いたら呼んでいる</b>と扱う
/// （書き分けたいなら、層の名前を出さずに書く。散文の「Application の例外」のように空白が続く形は当たらない）。</para>
/// <para><b>層のフォルダ名は 3 つに限り、名前空間はフォルダに一致させる。</b> 機能フォルダの直下に置けるのは
/// `Application`・`Infrastructure`・`Presentation` だけで、層の無いファイル（`Journals/X.cs`）は赤にする。
/// 名前空間がフォルダと違えば、同じ層のふりをして `using` 無しに別の層の型が使えるので、それも赤にする。</para>
/// </remarks>
public class LayerDependencyTests
{
    private const string RootNamespace = "BusinessApp.AccountingCore.Server";

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

    /// <summary>名前空間がフォルダと違うと、同じ層のふりをして別の層の型を短く使える（R67-04）。</summary>
    [Fact]
    public void ソースの名前空間はフォルダと一致している()
    {
        var mismatched = new List<string>();

        foreach (var path in SourceFiles())
        {
            var relative = Path.GetRelativePath(SourceProject, path);
            var segments = relative.Split(Path.DirectorySeparatorChar);
            if (segments.Length != 3)
            {
                continue;   // 置き場の誤りは上のテストが言う
            }

            var expected = $"{RootNamespace}.{segments[0]}.{segments[1]}";
            var match = Regex.Match(File.ReadAllText(path), @"^namespace\s+([\w.]+)\s*;", RegexOptions.Multiline);
            var declared = match.Success ? match.Groups[1].Value : "（なし）";
            if (declared != expected)
            {
                mismatched.Add($"{relative}: 宣言は {declared}、あるべきは {expected}");
            }
        }

        Assert.True(mismatched.Count == 0, string.Join(Environment.NewLine, mismatched));
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
                if (Mentions(text, forbidden))
                {
                    violations.Add($"{relative}: {layer} は {forbidden} を参照できない（ADR-0050 の決定 4）");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// <b>検査そのものを検査する。</b> 当たるべき形（完全修飾・短い `using`・短い修飾）と、
    /// 当たってはいけない形（散文・別の語の一部）を並べる。
    /// </summary>
    /// <remarks>
    /// <b>短い形を検体に持たなかった</b>ので、最初の版は `using Presentation;` を素通ししていた
    /// （2026-09-10 の自己レビュー R67-01）。規約テストは、規約を壊す最短の書き方を検体に持つ。
    /// </remarks>
    [Theory]
    [InlineData("using BusinessApp.AccountingCore.Server.Shared.Presentation;", true)]
    [InlineData("using Presentation;", true)]
    [InlineData("using Shared.Presentation;", true)]
    [InlineData("using static Shared.Presentation.SaveFailureMessage;", true)]
    [InlineData("var x = Presentation.JournalAmendmentEndpoint.Create(a);", true)]
    [InlineData("var t = Server.Shared.Presentation.SaveFailureMessage.Text;", true)]
    [InlineData("/// <see cref=\"Journals.Presentation.JournalAmendmentEndpoint\"/>", true)]
    [InlineData("// ここは Infrastructure なので、Presentation の例外を知らない", false)]
    [InlineData("// PresentationLayer.Foo は別の語", false)]
    [InlineData("var s = \"Presentation\";", false)]
    public void 参照の字面の判定(string text, bool expected)
        => Assert.Equal(expected, Mentions(text, "Presentation"));

    [Fact]
    public void 検査しているファイルが空でない()
        => Assert.True(SourceFiles().Count() > 20, "ソースを集められていない。");

    /// <summary>
    /// 層の名前が<b>名前空間として使われている</b>か——前に語が続かず、直後に `.` か `;` が来る。
    /// </summary>
    private static bool Mentions(string text, string layer)
        => Regex.IsMatch(text, @"(?<![\w])(?:[\w.]*\.)?" + layer + @"\s*[.;]");

    private static IEnumerable<string> SourceFiles()
        => Directory.EnumerateFiles(SourceProject, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(SourceProject, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));
}
