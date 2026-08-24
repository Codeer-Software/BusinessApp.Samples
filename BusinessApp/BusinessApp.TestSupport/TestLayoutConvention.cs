namespace BusinessApp.TestSupport;

using System.Text.RegularExpressions;

/// <summary>
/// テストの置き場所の規約（ADR-0012）を検査する。
/// </summary>
/// <remarks>
/// <para>ユニットテストは<b>対象と同じフォルダ構造・同じファイル名</b>に置く。
/// <c>Shared/Yen.cs</c> のテストは <c>Shared/YenTests.cs</c> であり、名前空間も揃える。</para>
/// <para>検査は「テスト → ソース」の一方向だけにしてある。逆方向（テストの無いソース）は
/// <b>カバレッジ 100% のゲートが受け持つ</b>ので、ここで許容リストを持つ必要がない。
/// 許容リストは必ず腐るので、持たなくて済む設計を選ぶ。</para>
/// <para><b>判定は返り値で返し、ここでは表明しない。</b> テストの書き方（xUnit）に
/// 縛られないので、テストプロジェクトが増えてもこの規約を 1 か所で持てる。</para>
/// </remarks>
public sealed class TestLayoutConvention
{
    /// <summary>ソースと対応しないテスト専用のフォルダ。</summary>
    private static readonly string[] ExemptDirectories = ["Fixtures", "Golden", "Conventions"];

    /// <param name="testProjectDirectory">テストプロジェクトのディレクトリ。</param>
    /// <param name="sourceProjectName">対応するソースプロジェクトのフォルダ名。</param>
    public TestLayoutConvention(string testProjectDirectory, string sourceProjectName)
    {
        TestProject = testProjectDirectory;
        TestProjectName = Path.GetFileName(testProjectDirectory.TrimEnd(Path.DirectorySeparatorChar));
        SourceProjectName = sourceProjectName;
        SourceProject = Path.Combine(Path.GetDirectoryName(TestProject)!, sourceProjectName);
    }

    public string TestProject { get; }

    public string TestProjectName { get; }

    public string SourceProject { get; }

    public string SourceProjectName { get; }

    /// <summary>対象のソースファイルが無いテストファイル。</summary>
    public IReadOnlyList<string> TestsWithoutSource()
        => MirroredTestFiles()
            .Select(file => (file.RelativePath, Expected: ExpectedSourcePath(file.RelativePath)))
            .Where(pair => !File.Exists(pair.Expected))
            .Select(pair => $"{TestProjectName}/{pair.RelativePath} に対応する "
                          + $"{SourceProjectName}/{Path.GetRelativePath(SourceProject, pair.Expected)} がない")
            .ToList();

    /// <summary>宣言した名前空間がフォルダ構造と合っていないテストファイル。</summary>
    public IReadOnlyList<string> TestsWithMismatchedNamespace()
        => MirroredTestFiles()
            .Select(file => (file.RelativePath, Declared: DeclaredNamespace(file.FullPath), Expected: ExpectedNamespace(file.RelativePath)))
            .Where(file => file.Declared != file.Expected)
            .Select(file => $"{file.RelativePath}: 宣言は {file.Declared ?? "（なし）"}、あるべきは {file.Expected}")
            .ToList();

    /// <summary>規約の対象となるテストファイル。</summary>
    public IReadOnlyList<(string FullPath, string RelativePath)> MirroredTestFiles()
        => Directory.EnumerateFiles(TestProject, "*Tests.cs", SearchOption.AllDirectories)
            .Select(path => (FullPath: path, RelativePath: Path.GetRelativePath(TestProject, path)))
            .Where(file => !IsExempt(file.RelativePath))
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToList();

    private string ExpectedSourcePath(string relativePath)
        => Path.Combine(SourceProject, relativePath[..^"Tests.cs".Length] + ".cs");

    private string ExpectedNamespace(string relativePath)
    {
        var folder = Path.GetDirectoryName(relativePath);
        return string.IsNullOrEmpty(folder)
            ? TestProjectName
            : $"{TestProjectName}.{folder.Replace(Path.DirectorySeparatorChar, '.')}";
    }

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

    /// <summary>
    /// 出力ディレクトリから遡って、呼び出し元のテストプロジェクトのディレクトリを探す。
    /// <c>CallerFilePath</c> を使うとビルドしたマシンの絶対パスがアセンブリに焼き込まれるので使わない
    /// （CLAUDE.md §5）。
    /// </summary>
    public static string FindProjectDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.csproj").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("テストプロジェクトのディレクトリを特定できない。");
    }
}
