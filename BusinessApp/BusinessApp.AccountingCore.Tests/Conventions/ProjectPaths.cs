namespace BusinessApp.AccountingCore.Tests.Conventions;

/// <summary>
/// 規約検査が使うプロジェクトの場所。
/// </summary>
/// <remarks>
/// 出力ディレクトリから遡って <c>.csproj</c> のある場所を探す。
/// <c>CallerFilePath</c> を使うとビルドしたマシンの絶対パスがアセンブリに焼き込まれるので使わない
/// （CLAUDE.md §5）。
/// </remarks>
public static class ProjectPaths
{
    public const string TestProjectName = "BusinessApp.AccountingCore.Tests";
    public const string SourceProjectName = "BusinessApp.AccountingCore";
    public const string RootNamespace = "BusinessApp.AccountingCore";

    public static string TestProject { get; } = FindProjectDirectory();

    public static string SourceProject { get; } =
        Path.Combine(Path.GetDirectoryName(TestProject)!, SourceProjectName);

    /// <summary>ビルド生成物を除いた、プロジェクト配下の C# ファイル。</summary>
    public static IEnumerable<string> SourceFiles(string projectDirectory)
        => Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(projectDirectory, path)));

    public static bool IsBuildOutput(string relativePath)
        => relativePath.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");

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
