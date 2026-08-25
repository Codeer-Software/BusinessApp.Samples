namespace BusinessApp.TestSupport;

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// C# の記法規約（ADR-0021）のうち、<b>アナライザで表現できないもの</b>を検査する。
/// </summary>
/// <remarks>
/// <para><c>.editorconfig</c> ＋ <c>EnforceCodeStyleInBuild</c> でビルドに載せられるのは
/// Roslyn が診断を持っているルールだけである。<c>is { }</c> の禁止のように
/// 「書ける形のうち特定のものを使わない」という規約には診断が無いので、
/// <b>ソースを読んで検査する</b>。</para>
/// <para><b>正規表現ではなく構文木で見る。</b> 最初は正規表現で書いたが、
/// <c>x is{}</c>（空白なし）・改行をまたぐ書き方・補間文字列の穴の中の引用符で、
/// <b>見逃しと消しすぎの両方</b>が起きた（2026-08-26 の自己レビュー）。
/// 構文木なら、コメントと文字列リテラルはそもそも式ではないので誤検知が原理的に起きず、
/// 空白と改行の入れ方にも左右されない。</para>
/// <para><b>判定は返り値で返し、ここでは表明しない。</b>
/// <see cref="TestLayoutConvention"/> と同じ作法（テストの書き方に縛られない）。</para>
/// </remarks>
public sealed class CSharpStyleConvention(string repositoryRoot)
{
    /// <summary>記法規約をビルドで強制するプロジェクト（ADR-0021 §4-1）。</summary>
    /// <remarks>
    /// ここに載っているプロジェクトは <c>TreatWarningsAsErrors</c> と
    /// <c>EnforceCodeStyleInBuild</c> の両方を宣言し、かつ
    /// <c>.editorconfig</c> の強制セクションのパスにも載っていなければならない。
    /// </remarks>
    public static readonly string[] EnforcedProjects =
    [
        "BusinessApp.AccountingCore",
        "BusinessApp.AccountingCore.Client",
        "BusinessApp.AccountingCore.Server",
        "BusinessApp.AccountingCore.Server.Tests",
        "BusinessApp.AccountingCore.Tests",
        "BusinessApp.Schema.Tests",
        "BusinessApp.SchemaVerifyCli",
        "BusinessApp.TestSupport",
    ];

    /// <summary>CLB テンプレート由来のプロジェクト（ADR-0021 §3）。</summary>
    /// <remarks>
    /// テンプレート更新との差分を無用に増やさないため、記法をビルドで強制しない。
    /// <b>ただし <see cref="ForbiddenFormSet.Everywhere"/> の禁止形だけは、ここにも効く。</b>
    /// </remarks>
    public static readonly string[] TemplateDerivedProjects =
    [
        "BusinessApp.Client",
        "BusinessApp.Client.Shared",
        "BusinessApp.Designer",
        "BusinessApp.LicenseRegisterCli",
        "BusinessApp.Server",
    ];

    /// <summary>利用者に見せる文言を組み立てる層。</summary>
    public static readonly string[] MessageLayerProjects =
    [
        "BusinessApp.AccountingCore",
        "BusinessApp.AccountingCore.Server",
    ];

    /// <summary>csproj に宣言されていなければならないプロパティ（ADR-0021 §4-1）。</summary>
    private static readonly string[] RequiredProperties = ["TreatWarningsAsErrors", "EnforceCodeStyleInBuild"];

    /// <summary><c>.editorconfig</c> の強制セクションの見出し。</summary>
    /// <remarks>
    /// <c>[BusinessApp/BusinessApp.{A,B,C}/**.cs]</c> の中かっこの中を取り出す。
    /// この見出しが無い、または中身が <see cref="EnforcedProjects"/> とずれていると、
    /// <c>EnforceCodeStyleInBuild</c> だけが有効で <b>severity を上げる指定がどこにも無い</b>
    /// という状態になり、ビルドが素通りする。
    /// </remarks>
    private static readonly Regex EnforcementSection =
        new(@"^\[BusinessApp/BusinessApp\.\{(?<names>[^}]+)\}/\*\*\.cs\]$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public string RepositoryRoot { get; } = repositoryRoot;

    /// <summary>プロジェクトを置いてあるディレクトリ。</summary>
    public string ProjectsDirectory => Path.Combine(RepositoryRoot, "BusinessApp");

    /// <summary>ソリューション（＝ビルドされるものの正典）。</summary>
    public string SolutionFile => Path.Combine(RepositoryRoot, "BusinessApp.slnx");

    /// <summary>記法の強制の土台。</summary>
    public string EditorConfigFile => Path.Combine(RepositoryRoot, ".editorconfig");

    /// <summary>禁止形を使っている箇所。</summary>
    public IReadOnlyList<string> ForbiddenFormUsages()
        => SourceFiles(RepositoryRoot)
            .SelectMany(file => FindForbiddenForms(File.ReadAllText(file), SetsFor(file), IsClbScript(file))
                .Select(found => (File: file, found.Line, found.Message)))
            .OrderBy(usage => usage.File, StringComparer.Ordinal).ThenBy(usage => usage.Line)
            .Select(usage => $"{usage.File}({usage.Line}): {usage.Message}")
            .ToList();

    /// <summary>
    /// ソース 1 本に禁止形を当てる。<b>ファイルの走査もテストも、必ずここを通る。</b>
    /// </summary>
    /// <remarks>
    /// 走査だけが通る別経路を作らないこと。<b>テストが本物と違う道を通ると、
    /// 「テストは緑だが本番は何も見ていない」が起きる</b>。
    /// </remarks>
    /// <param name="source">C# のソース。</param>
    /// <param name="sets">当てる禁止形の組。</param>
    /// <param name="isClbScript">
    /// CLB スクリプト（<c>*.mod.cs</c>）か。クラスの外にメソッドを書くので解析の種類が違う。
    /// </param>
    public static IReadOnlyList<(int Line, string Message)> FindForbiddenForms(
        string source, ForbiddenFormSet sets, bool isClbScript = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (sets == ForbiddenFormSet.None)
        {
            return [];
        }

        var options = new CSharpParseOptions(
            LanguageVersion.CSharp12,
            kind: isClbScript ? SourceCodeKind.Script : SourceCodeKind.Regular);
        var root = CSharpSyntaxTree.ParseText(source, options).GetRoot();
        var found = new List<(int Line, string Message)>();

        if (sets.HasFlag(ForbiddenFormSet.Everywhere))
        {
            // `is { }` / `is not { }` / `is{}`。型も位置パターンも無く、中身が空のプロパティパターン。
            // `is string { }` や `is { Length: 0 }` は別の意味なので当たらない。
            Report(
                root.DescendantNodes().OfType<RecursivePatternSyntax>().Where(IsEmptyPropertyPattern),
                "is { }",
                "null 検査であることが字面から読めない。型パターン（is int v）を使う（ADR-0021 §2）");
        }

        if (sets.HasFlag(ForbiddenFormSet.EnforcedProject))
        {
            // `x == null` / `x != null` / `null == x`（Yoda 形も同じ）。
            Report(
                root.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(IsNullComparison),
                "== null / != null",
                "演算子の多重定義に左右される。is null / is not null を使う（ADR-0021 §2）");
        }

        if (sets.HasFlag(ForbiddenFormSet.MessageLayer))
        {
            Report(
                root.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Where(IsEnvironmentNewLine),
                "Environment.NewLine",
                "利用者に見せる文言の改行は raw string literal の改行（LF）に統一する（ADR-0021 §2）");
        }

        return found.OrderBy(entry => entry.Line).ToList();

        void Report(IEnumerable<SyntaxNode> nodes, string name, string reason)
            => found.AddRange(nodes.Select(node =>
                (node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                 $"{name} は使わない — {reason}")));
    }

    /// <summary>記法の強制を宣言していないプロジェクト。</summary>
    /// <remarks>
    /// <b>csproj を XML として読む。</b> 文字列の部分一致だと、コメントアウトされた宣言や
    /// <c>Condition</c> つきの <c>PropertyGroup</c>（＝特定の構成でしか効かない宣言）を
    /// 「有る」と数えてしまう。
    /// </remarks>
    public IReadOnlyList<string> ProjectsWithoutEnforcement()
        => EnforcedProjects
            .SelectMany(name => MissingProperties(name, Path.Combine(ProjectsDirectory, name, name + ".csproj")))
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToList();

    /// <summary>どちらの表にも載っていないプロジェクト（＝黙って検査の外にいるもの）。</summary>
    /// <remarks>
    /// <b>実体はソリューション（<c>BusinessApp.slnx</c>）である。</b> ビルドされるのは
    /// ソリューションに載っているものであって、フォルダの有無ではない。
    /// ディレクトリを数えると、入れ子に置いたプロジェクトや
    /// <c>BusinessApp/</c> の外に置いたプロジェクトが黙って検査を外れる。
    /// </remarks>
    public IReadOnlyList<string> ProjectsMissingFromTable()
    {
        var listed = EnforcedProjects.Concat(TemplateDerivedProjects).ToHashSet(StringComparer.Ordinal);

        return SolutionProjects()
            .Where(project => !listed.Contains(project.Name))
            .Select(project => $"{project.Name}（{project.Path}）がどちらの表にも載っていない（ADR-0021 §3 のどちらかに足す）")
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary><c>.editorconfig</c> の強制セクションと <see cref="EnforcedProjects"/> のずれ。</summary>
    /// <remarks>
    /// <b>ここがずれると、csproj も表も正しいのにビルドが素通りする。</b>
    /// <c>EnforceCodeStyleInBuild</c> は「アナライザを走らせる」だけで、
    /// どの規則をエラーにするかは <c>.editorconfig</c> のセクションが決めるからである。
    /// ADR-0021 §4-1 が実測で踏んだ形（severity の指定が効いていない）と同型なので、
    /// <b>両方向</b>（不足・余剰）で突き合わせる。
    /// </remarks>
    public IReadOnlyList<string> EditorConfigMismatches()
    {
        if (!File.Exists(EditorConfigFile))
        {
            return [".editorconfig が無い。記法の強制はこのファイルが土台である（ADR-0021 §4-1）"];
        }

        var match = EnforcementSection.Match(File.ReadAllText(EditorConfigFile).ReplaceLineEndings("\n"));
        if (!match.Success)
        {
            return [".editorconfig に強制セクションの見出しが無い（ADR-0021 §4-1）"];
        }

        var declared = match.Groups["names"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => "BusinessApp." + name)
            .ToHashSet(StringComparer.Ordinal);
        var expected = EnforcedProjects.ToHashSet(StringComparer.Ordinal);

        return
        [
            .. expected.Except(declared).Order(StringComparer.Ordinal)
                .Select(name => $"{name}: .editorconfig の強制セクションに無い（規則の severity が 1 つも当たらない）"),
            .. declared.Except(expected).Order(StringComparer.Ordinal)
                .Select(name => $"{name}: .editorconfig の強制セクションにあるが EnforcedProjects に無い"),
        ];
    }

    /// <summary>改行が CRLF になっている C#。</summary>
    /// <remarks>
    /// <b>raw string literal の改行はソースファイルの改行そのもの</b>なので、
    /// CRLF のまま書かれた文言は改行コードが割れる（ADR-0021 §2）。
    /// <c>.gitattributes</c> の <c>*.cs text eol=lf</c> は checkout のときに効くが、
    /// <b>既に CRLF で checkout 済みの作業コピーは直らない</b>し、
    /// エディタが CRLF で書き足しても IDE0055 は改行コードを見ない。
    /// ここで見て初めて、<c>.gitattributes</c> の効き目を機械が確かめたことになる。
    /// </remarks>
    public IReadOnlyList<string> FilesWithCarriageReturns()
        => SourceFiles(RepositoryRoot)
            .Where(file => File.ReadAllText(file).Contains('\r'))
            .Select(file => $"{file}: 改行が CRLF になっている（LF に直す。ADR-0021 §4-3）")
            .OrderBy(message => message, StringComparer.Ordinal)
            .ToList();

    /// <summary>ソリューションに載っているプロジェクト。</summary>
    public IReadOnlyList<(string Name, string Path)> SolutionProjects()
        => XDocument.Load(SolutionFile)
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrEmpty(path))
            .Select(path => (Name: System.IO.Path.GetFileNameWithoutExtension(path)!, Path: path!))
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>そのファイルに当てる禁止形の組。</summary>
    public ForbiddenFormSet SetsFor(string file)
    {
        var sets = ForbiddenFormSet.Everywhere;
        var project = ProjectOf(file);
        if (project is null)
        {
            return sets;
        }

        if (EnforcedProjects.Contains(project, StringComparer.Ordinal))
        {
            sets |= ForbiddenFormSet.EnforcedProject;
        }

        if (MessageLayerProjects.Contains(project, StringComparer.Ordinal))
        {
            sets |= ForbiddenFormSet.MessageLayer;
        }

        return sets;
    }

    /// <summary>そのファイルが属するプロジェクト（<c>BusinessApp/</c> の外なら null）。</summary>
    public string? ProjectOf(string file)
    {
        var relative = Path.GetRelativePath(ProjectsDirectory, file);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Length > 1 ? segments[0] : null;
    }

    /// <summary>CLB スクリプト（クラスの外にメソッドを書くので、解析の種類が違う）。</summary>
    public static bool IsClbScript(string file)
        => file.EndsWith(".mod.cs", StringComparison.Ordinal);

    /// <summary>ビルド生成物と生成フォルダを除いた C# ファイル。</summary>
    /// <remarks>
    /// <c>Designer/ClaudeCodeForDesigner/</c> はデザイナが再生成する生成物（Git 追跡外）なので見ない。
    /// <c>StrykerOutput</c> と <c>TestResults</c> も、ツールが吐いた写しを検査しても意味が無いので見ない。
    /// </remarks>
    public static IEnumerable<string> SourceFiles(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsExcluded(Path.GetRelativePath(directory, path)))
            : [];

    /// <summary>
    /// 出力ディレクトリから遡ってリポジトリのルート（<c>BusinessApp.slnx</c> のある場所）を探す。
    /// </summary>
    /// <remarks>
    /// <c>CallerFilePath</c> を使うとビルドしたマシンの絶対パスがアセンブリに焼き込まれるので使わない
    /// （CLAUDE.md §5）。
    /// </remarks>
    public static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("BusinessApp.slnx").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("リポジトリのルート（BusinessApp.slnx のある場所）を特定できない。");
    }

    /// <summary>型も位置パターンも無く、中身が空のプロパティパターン（＝<c>is { }</c>）。</summary>
    private static bool IsEmptyPropertyPattern(RecursivePatternSyntax pattern)
        => pattern.Type is null
           && pattern.PositionalPatternClause is null
           && pattern.PropertyPatternClause is not null
           && pattern.PropertyPatternClause.Subpatterns.Count == 0;

    /// <summary><c>x == null</c> / <c>x != null</c>（左右どちらに null があっても）。</summary>
    private static bool IsNullComparison(BinaryExpressionSyntax expression)
        => (expression.IsKind(SyntaxKind.EqualsExpression) || expression.IsKind(SyntaxKind.NotEqualsExpression))
           && (IsNullLiteral(expression.Left) || IsNullLiteral(expression.Right));

    private static bool IsNullLiteral(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.NullLiteralExpression);

    private static bool IsEnvironmentNewLine(MemberAccessExpressionSyntax access)
        => access.Name.Identifier.ValueText == "NewLine"
           && access.Expression.ToString() is "Environment" or "System.Environment";

    private static IEnumerable<string> MissingProperties(string project, string csproj)
    {
        if (!File.Exists(csproj))
        {
            yield return $"{project}: {Path.GetFileName(csproj)} が見つからない";
            yield break;
        }

        // Condition つきの PropertyGroup は特定の構成でしか効かないので数えない。
        var unconditional = XDocument.Load(csproj)
            .Descendants("PropertyGroup")
            .Where(group => group.Attribute("Condition") is null)
            .SelectMany(group => group.Elements())
            .ToList();

        foreach (var property in RequiredProperties)
        {
            var declared = unconditional
                .Where(element => element.Name.LocalName == property)
                .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

            if (!declared)
            {
                yield return $"{project}: <{property}>true</{property}> が Condition 無しの PropertyGroup に無い（ADR-0021 §4-1）";
            }
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            segment is "bin" or "obj" or ".vs" or ".git" or "StrykerOutput" or "TestResults" or "ClaudeCodeForDesigner");
    }
}

/// <summary>禁止形の適用範囲（ADR-0021 §4-2）。</summary>
[Flags]
public enum ForbiddenFormSet
{
    None = 0,

    /// <summary>リポジトリ内のすべての C#（CLB スクリプトを含む）。</summary>
    Everywhere = 1,

    /// <summary>ビルドの関門を敷いたプロジェクト。</summary>
    EnforcedProject = 2,

    /// <summary>利用者に見せる文言を組み立てる層。</summary>
    MessageLayer = 4,
}
