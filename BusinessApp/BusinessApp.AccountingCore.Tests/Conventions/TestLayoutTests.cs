namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Text.RegularExpressions;

/// <summary>
/// テストの置き場所の規約（ADR-0012）。
/// </summary>
/// <remarks>
/// <para>ユニットテストは<b>対象と同じフォルダ構造・同じファイル名</b>に置く。
/// <c>Primitives/Yen.cs</c> のテストは <c>Primitives/YenTests.cs</c> であり、名前空間も揃える。</para>
/// <para>検査は「テスト → ソース」の一方向だけにしてある。逆方向（テストの無いソース）は
/// <b>カバレッジ 100% のゲートが受け持つ</b>ので、ここで許容リストを持つ必要がない。
/// 許容リストは必ず腐るので、持たなくて済む設計を選ぶ。</para>
/// <para>この検査を CLI ではなくテストに置いたのは、<c>dotnet test</c> が必ず流れるからである。
/// 別立ての CLI は「流し忘れ」で静かに形骸化する。</para>
/// </remarks>
public class TestLayoutTests
{
    /// <summary>ソースと対応しないテスト専用のフォルダ。</summary>
    private static readonly string[] ExemptDirectories = ["Fixtures", "Golden", "Conventions"];

    private const string TestProjectName = "BusinessApp.AccountingCore.Tests";
    private const string SourceProjectName = "BusinessApp.AccountingCore";

    [Fact]
    public void テストファイルは対象と同じ場所と名前に置かれている()
    {
        var missing = new List<string>();

        foreach (var (testFile, relativePath) in MirroredTestFiles())
        {
            var expectedSource = Path.Combine(SourceProjectDirectory, relativePath[..^"Tests.cs".Length] + ".cs");
            if (!File.Exists(expectedSource))
            {
                missing.Add($"{TestProjectName}/{relativePath} に対応する {SourceProjectName}/{Path.GetRelativePath(SourceProjectDirectory, expectedSource)} がない");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void テストの名前空間はフォルダ構造と一致している()
    {
        var mismatched = new List<string>();

        foreach (var (testFile, relativePath) in MirroredTestFiles())
        {
            var folder = Path.GetDirectoryName(relativePath);
            var expected = string.IsNullOrEmpty(folder)
                ? TestProjectName
                : $"{TestProjectName}.{folder.Replace(Path.DirectorySeparatorChar, '.')}";

            var declared = DeclaredNamespace(testFile);
            if (declared != expected)
            {
                mismatched.Add($"{relativePath}: 宣言は {declared ?? "（なし）"}、あるべきは {expected}");
            }
        }

        Assert.True(mismatched.Count == 0, string.Join(Environment.NewLine, mismatched));
    }

    [Fact]
    public void テストプロジェクトとソースプロジェクトを見つけられる()
    {
        // 以降の検査が「ファイルが 1 つも見つからず素通り」で緑にならないための土台。
        Assert.True(Directory.Exists(SourceProjectDirectory), $"{SourceProjectName} が見つからない");
        Assert.NotEmpty(MirroredTestFiles());
    }

    private static IReadOnlyList<(string FullPath, string RelativePath)> MirroredTestFiles()
        => Directory.EnumerateFiles(TestProjectDirectory, "*Tests.cs", SearchOption.AllDirectories)
            .Select(path => (FullPath: path, RelativePath: Path.GetRelativePath(TestProjectDirectory, path)))
            .Where(file => !IsExempt(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();

    private static bool IsExempt(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar);
        return segments.Any(segment =>
            segment is "bin" or "obj" || ExemptDirectories.Contains(segment, StringComparer.Ordinal));
    }

    private static string? DeclaredNamespace(string filePath)
    {
        var match = Regex.Match(File.ReadAllText(filePath), @"^namespace\s+([\w.]+)\s*;", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string TestProjectDirectory { get; } = FindProjectDirectory();

    private static string SourceProjectDirectory { get; } =
        Path.Combine(Path.GetDirectoryName(TestProjectDirectory)!, SourceProjectName);

    /// <summary>
    /// 出力ディレクトリから遡って <c>.csproj</c> のある場所を探す。
    /// <c>CallerFilePath</c> を使うとビルドしたマシンの絶対パスがアセンブリに焼き込まれるので使わない。
    /// </summary>
    private static string FindProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.csproj").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"{TestProjectName} のプロジェクトディレクトリを特定できない。");
    }
}
