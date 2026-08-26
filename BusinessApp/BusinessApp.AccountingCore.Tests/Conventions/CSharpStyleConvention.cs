namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Collections.Immutable;
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
/// <b>見逃しと消しすぎの両方</b>が起きた（qa/02 R8-01）。
/// 構文木なら、コメントと文字列リテラルはそもそも式ではないので誤検知が原理的に起きず、
/// 空白と改行の入れ方にも左右されない。</para>
/// <para><b>判定はすべて「入力を受け取って結果を返す」形にしてある。</b>
/// ディスクを読むのは <c>*Usages</c> / <c>*Problems</c> の薄い皮だけで、
/// 中身は文字列を渡せば試せる。<b>そうしないと「壊した入力を食わせて赤になる」検査が書けず、
/// 中身を空にしても緑のまま</b>という状態になる（qa/02 R8-11）。</para>
/// <para><b>テストプロジェクトに置いてある。</b> <see cref="TestSupport.TestLayoutConvention"/> と違って
/// 使うのはこのプロジェクトだけで、共有の土台に置くと Roslyn の DLL（9 MB）が
/// <c>SchemaVerifyCli</c> の出力にまで載る。</para>
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

    /// <summary>
    /// 強制対象のプロジェクト数の下限。<b>ラチェットである。</b>
    /// </summary>
    /// <remarks>
    /// 3 つの表（csproj・<c>.editorconfig</c>・<see cref="EnforcedProjects"/>）は互いだけを
    /// 照合しているので、<b>3 か所を揃えて動かすと、整合したまま関門だけが消える</b>（qa/02 R8-12）。
    /// ミューテーションスコアの下限（ADR-0012）と同じ作法で置く。
    /// <b>下げるときは黙って下げず、理由を書いて下げる。</b>
    /// </remarks>
    public const int MinimumEnforcedProjects = 8;

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
    /// <remarks>
    /// <c>BusinessApp.Server</c> は CLB テンプレート由来だが、
    /// <c>UseExceptionHandlerSendToFront</c> が例外の文面を組み立てて画面へ返すので、
    /// <b>この層でも改行コードが割れる</b>（qa/02 R8-20）。ここだけは対象に入れる。
    /// </remarks>
    public static readonly string[] MessageLayerProjects =
    [
        "BusinessApp.AccountingCore",
        "BusinessApp.AccountingCore.Server",
        "BusinessApp.Server",
    ];

    /// <summary>
    /// <b>ビルドで必ず警告になっていなければならない</b>規則。
    /// </summary>
    /// <remarks>
    /// <para><c>.editorconfig</c> の見出しだけを照合しても、<b>中身の severity 行が
    /// 全部消えていることには気づけない</b>（qa/02 R8-10）。ここに挙げた規則について、
    /// Roslyn に <c>.editorconfig</c> を解釈させ、<b>実効の severity</b> を突き合わせる。</para>
    /// <para>選び方は「ADR-0021 §2 の対応表が決めたもの」＋
    /// 「読み手のコストを直接下げる整形と未使用の検出」である。
    /// 全規則を写すと二重管理になるので<b>代表だけ</b>を名指しし、
    /// 名指ししていない分は <see cref="MinimumWarnRules"/> の数で押さえる。</para>
    /// </remarks>
    public static readonly string[] MustBeWarnings =
    [
        "IDE0055",   // 整形
        "IDE0161",   // file-scoped 名前空間
        "IDE0090",   // target-typed new
        "IDE0290",   // primary constructor
        "IDE0300",   // コレクション式
        "IDE0066",   // switch 式
        "IDE0083",   // not パターン
        "IDE0040",   // アクセシビリティ修飾子
        "IDE0044",   // readonly フィールド
        "IDE0060",   // 使われない引数
    ];

    /// <summary>
    /// 規則と、<b>それが「何を正とするか」を決める option</b>（ADR-0021 §4-1 の 2 段）。
    /// </summary>
    /// <remarks>
    /// severity は「報告するか」しか決めない。<c>csharp_style_namespace_declarations</c> を
    /// <c>block_scoped</c> にすると、<c>IDE0161</c> は warning のまま**逆のこと**を言う。
    /// <b>上段の option を 6 行反転しただけで、10 個のビルドエラーが消えたのにテストは全部緑だった</b>
    /// （qa/02 R8-25。レビュアが実測）。だから両方を見る。
    /// </remarks>
    public static readonly (string Rule, string Option, string Value)[] RuleOptions =
    [
        ("IDE0161", "csharp_style_namespace_declarations", "file_scoped"),
        ("IDE0290", "csharp_style_prefer_primary_constructors", "true"),
        ("IDE0090", "csharp_style_implicit_object_creation_when_type_is_apparent", "true"),
        ("IDE0300", "dotnet_style_prefer_collection_expression", "when_types_loosely_match"),
        ("IDE0044", "dotnet_style_readonly_field", "true"),
        ("IDE0066", "csharp_style_prefer_switch_expression", "true"),
        ("IDE0083", "csharp_style_prefer_not_pattern", "true"),
        ("IDE0040", "dotnet_style_require_accessibility_modifiers", "for_non_interface_members"),
    ];

    /// <summary>
    /// 強制対象で警告になっている規則数の下限。<b>ラチェットである。</b>
    /// </summary>
    /// <remarks>
    /// 名指しで守っているのは <see cref="MustBeWarnings"/> の 10 件だけで、
    /// <c>.editorconfig</c> にはその 4 倍以上の規則がある。**名指ししていない分を全部消しても
    /// 検査は緑になる**ので、プロジェクト数と同じ作法で数に下限を置く（qa/02 R8-29）。
    /// <b>下げるときは黙って下げず、qa/02 に理由を書いて下げる。</b>
    /// </remarks>
    public const int MinimumWarnRules = 45;

    /// <summary>検討して<b>強制しないと決めた</b>規則（ADR-0021 §4-1）。</summary>
    /// <remarks>黙って警告に格上げされると、決めたことが崩れたのに誰も気づかない。</remarks>
    public static readonly string[] MustNotBeWarnings = ["IDE0305", "IDE0045", "IDE0046"];

    /// <summary>csproj に宣言されていなければならないプロパティ（ADR-0021 §4-1）。</summary>
    private static readonly string[] RequiredProperties = ["TreatWarningsAsErrors", "EnforceCodeStyleInBuild"];

    /// <summary>
    /// 宣言があっても関門を無効にできてしまうプロパティ。
    /// </summary>
    /// <remarks>
    /// <c>TreatWarningsAsErrors</c> と <c>EnforceCodeStyleInBuild</c> を残したまま
    /// <c>&lt;NoWarn&gt;IDE0055&lt;/NoWarn&gt;</c> を足せば関門は死ぬ（qa/02 R8-13）。
    /// <b>「必要なプロパティが有る」ことは「関門が効いている」ことを意味しない。</b>
    /// 抑制したいときは <c>#pragma</c> で 1 か所ずつ理由と一緒に（IDE0079 が不要な抑制を見張る）。
    /// </remarks>
    private static readonly string[] SuppressingProperties = ["NoWarn", "WarningsNotAsErrors"];

    /// <summary><c>.editorconfig</c> の強制セクションの見出し。</summary>
    private static readonly Regex EnforcementSection =
        new(@"^\[BusinessApp/BusinessApp\.\{(?<names>[^}]+)\}/\*\*\.cs\]$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary><c>.gitattributes</c> の C# の改行の指定（<b>後勝ち</b>なので最後のものが効く）。</summary>
    private static readonly Regex CSharpEolRule =
        new(@"^" + Regex.Escape("*.cs") + @"\s+text\s+eol=(?<eol>" + @"\S+)\s*$",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

    public string RepositoryRoot { get; } = repositoryRoot;

    /// <summary>プロジェクトを置いてあるディレクトリ。</summary>
    public string ProjectsDirectory => Path.Combine(RepositoryRoot, "BusinessApp");

    /// <summary>ソリューション（＝ビルドされるものの正典）。</summary>
    public string SolutionFile => Path.Combine(RepositoryRoot, "BusinessApp.slnx");

    // -----------------------------------------------------------------------
    // ディスクから読むもの（**判定は持たない。**「どこを読むか」だけを持つ）
    // -----------------------------------------------------------------------
    //
    // 判定と読み取りを混ぜた「薄い皮」を作らないこと。皮には「どこを読むか」と
    // 「何と何を足すか」という判断が残り、そこは中身を空にしても緑のまま通る
    // （qa/02 R8-33。走査範囲を 1 語変えると Designer/*.mod.cs が黙って外れた）。
    // ここに置いたものはすべてテストが表明する。

    /// <summary>検査するソース（パスと中身）。</summary>
    public IReadOnlyList<(string Path, string Source)> FilesToScan()
        => [.. SourceFiles(RepositoryRoot).Select(file => (file, File.ReadAllText(file)))];

    /// <summary>
    /// リポジトリ内のすべての <c>.editorconfig</c>（浅い順）。
    /// </summary>
    /// <remarks>
    /// <b>ルートの 1 本だけを見てはいけない。</b> コンパイラはソースから上へ全部を積むので、
    /// 下の階層に 3 行置くだけで関門を殺せる（qa/02 R8-26）。
    /// </remarks>
    public IReadOnlyList<(string Path, string Text)> EditorConfigFiles()
        => [.. ConfigFiles(".editorconfig")];

    /// <summary>リポジトリ内のすべての <c>.gitattributes</c>（浅い順）。</summary>
    public IReadOnlyList<(string Path, string Text)> GitAttributesFiles()
        => [.. ConfigFiles(".gitattributes")];

    /// <summary>
    /// MSBuild が暗黙に読み込む設定（<c>Directory.Build.props</c> / <c>.targets</c>）。
    /// </summary>
    /// <remarks>
    /// ここに <c>&lt;NoWarn&gt;</c> を書けば、csproj を 1 文字も触らずに関門を殺せる（qa/02 R8-27）。
    /// </remarks>
    public IReadOnlyList<string> ImplicitBuildProperties()
        => [.. ConfigFiles("Directory.Build.props").Concat(ConfigFiles("Directory.Build.targets"))
            .Select(file => file.Text)];

    /// <summary>プロジェクトの csproj（無ければ null）。</summary>
    public string? ProjectFile(string project)
        => ReadIfExists(Path.Combine(ProjectsDirectory, project, project + ".csproj"));

    /// <summary>ソリューションに載っているプロジェクト。</summary>
    public IReadOnlyList<(string Name, string Path)> SolutionProjects()
        => SolutionProjectsIn(File.ReadAllText(SolutionFile));

    private IEnumerable<(string Path, string Text)> ConfigFiles(string name)
        => Directory.Exists(RepositoryRoot)
            ? Directory.EnumerateFiles(RepositoryRoot, name, SearchOption.AllDirectories)
                .Where(path => !IsExcluded(Path.GetRelativePath(RepositoryRoot, path)))
                .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.Ordinal)
                .Select(path => (path, File.ReadAllText(path)))
            : [];

    // -----------------------------------------------------------------------
    // 純粋な判定（テストは必ずこちらを通る）
    // -----------------------------------------------------------------------

    /// <summary>
    /// ソース 1 本に禁止形を当てる。<b>ファイルの走査もテストも、必ずここを通る。</b>
    /// </summary>
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

        // `#if` の中は disabled trivia になって構文木から消える。DEBUG だけは実際にビルドされる
        // 構成なので通しておき、そのうえで `#if` そのものを禁止形にしてある（下の EnforcedProject）。
        var options = new CSharpParseOptions(
            LanguageVersion.CSharp12,
            kind: isClbScript ? SourceCodeKind.Script : SourceCodeKind.Regular,
            preprocessorSymbols: ["DEBUG"]);
        var tree = CSharpSyntaxTree.ParseText(source, options);
        var root = tree.GetRoot();
        var found = new List<(int Line, string Message)>();

        // **読めなかったことを「違反 0 件」と混同しない。**
        // 構文が壊れていれば当然どの禁止形にも当たらず、見た目は「きれい」になる。
        foreach (var diagnostic in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            found.Add((
                diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
                $"構文を読めないので検査できない — {diagnostic.Id}: {diagnostic.GetMessage()}"));
        }

        if (sets.HasFlag(ForbiddenFormSet.Everywhere))
        {
            // `is { }` / `is not { }` / `is{}`。型も位置パターンも無く、中身が空のプロパティパターン。
            // `is string { }` や `is { Length: 0 }` は別の意味なので当たらない。
            ReportNodes(
                root.DescendantNodes().OfType<RecursivePatternSyntax>().Where(IsEmptyPropertyPattern),
                "is { }",
                "null 検査であることが字面から読めない。型パターン（is int v）を使う（ADR-0021 §2）");

            // `#if` の中は構文木から消えるので、**そこに書かれた禁止形は検査を素通りする**。
            // 分岐の網羅という難問を持ち込むより、条件付きコンパイルそのものを使わない。
            // **理屈は適用範囲の広い Everywhere にこそ当てはまる**（qa/02 R8-30）。
            ReportLines(
                root.DescendantTrivia().Where(IsConditionalDirective).Select(Line),
                "#if / #elif / #else",
                "条件付きコンパイルの中は構文木から消え、記法の検査が素通りする（ADR-0021 §4-2）");
        }

        if (sets.HasFlag(ForbiddenFormSet.EnforcedProject))
        {
            // `x == null` / `x != null` / `null == x`。括弧とキャストで包んでも同じ。
            ReportNodes(
                root.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(IsNullComparison),
                "== null / != null",
                "演算子の多重定義に左右される。is null / is not null を使う（ADR-0021 §2）");
        }

        if (sets.HasFlag(ForbiddenFormSet.MessageLayer))
        {
            ReportNodes(
                EnvironmentNewLineUsages(root),
                "Environment.NewLine",
                "利用者に見せる文言の改行は raw string literal の改行（LF）に統一する（ADR-0021 §2）");

            // `Environment.NewLine` を禁じても、CR を書いた文字列リテラルなら同じ結果になる。
            ReportNodes(
                CarriageReturnLiterals(root),
                "リテラルの中の CR",
                "利用者に見せる文言の改行は LF に統一する（ADR-0021 §2）");
        }

        return [.. found.OrderBy(entry => entry.Line)];

        void ReportNodes(IEnumerable<SyntaxNode> nodes, string name, string reason)
            => ReportLines(nodes.Select(Line), name, reason);

        void ReportLines(IEnumerable<int> lines, string name, string reason)
            => found.AddRange(lines.Distinct().Select(line => (line, $"{name} は使わない — {reason}")));
    }

    /// <summary>ソースの集まりに、置き場所ごとの禁止形を当てる。</summary>
    /// <remarks>
    /// <b>走査と <see cref="SetsFor"/> をつなぐ配線もここで検査できるようにしてある。</b>
    /// ディスクを読む側にしか無いと、配線を間違えても誰も気づかない。
    /// </remarks>
    public IReadOnlyList<string> ForbiddenFormUsagesIn(IEnumerable<(string Path, string Source)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return
        [
            .. files
                .SelectMany(file => FindForbiddenForms(file.Source, SetsFor(file.Path), IsClbScript(file.Path))
                    .Select(found => (file.Path, found.Line, found.Message)))
                .OrderBy(usage => usage.Path, StringComparer.Ordinal).ThenBy(usage => usage.Line)
                .Select(usage => $"{usage.Path}({usage.Line}): {usage.Message}"),
        ];
    }

    /// <summary>csproj が記法の強制を宣言し、かつそれを打ち消していないか。</summary>
    /// <param name="project">プロジェクト名。</param>
    /// <param name="csproj">csproj の中身（無ければ null）。</param>
    /// <param name="implicitProperties">
    /// MSBuild が暗黙に読み込む設定（<c>Directory.Build.props</c> など）の中身。
    /// <b>ここに <c>&lt;NoWarn&gt;</c> を書けば、csproj を 1 文字も触らずに関門を殺せる</b>（qa/02 R8-27）。
    /// 必要プロパティの側は集約されると誤検知（＝安全側）だが、<b>抑制の側は逆向きに壊れる</b>。
    /// </param>
    public static IReadOnlyList<string> EnforcementProblems(
        string project, string? csproj, IEnumerable<string>? implicitProperties = null)
    {
        if (csproj is null)
        {
            return [$"{project}: csproj が見つからない"];
        }

        // Condition つきの PropertyGroup は特定の構成でしか効かないので数えない。
        var properties = XDocument.Parse(csproj)
            .Descendants("PropertyGroup")
            .Where(group => group.Attribute("Condition") is null)
            .SelectMany(group => group.Elements())
            .ToList();

        return
        [
            .. RequiredProperties
                .Where(property => !properties
                    .Where(element => element.Name.LocalName == property)
                    .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
                .Select(property =>
                    $"{project}: <{property}>true</{property}> が Condition 無しの PropertyGroup に無い（ADR-0021 §4-1）"),
            .. Suppressions(project, properties, "csproj"),
            .. (implicitProperties ?? []).SelectMany(text => Suppressions(
                project, AllProperties(text), "暗黙に読み込まれる設定")),
        ];
    }

    /// <summary><c>.editorconfig</c> の強制セクションの見出しと <see cref="EnforcedProjects"/> のずれ。</summary>
    public static IReadOnlyList<string> EditorConfigMismatches(string? editorConfig)
    {
        if (editorConfig is null)
        {
            return [".editorconfig が無い。記法の強制はこのファイルが土台である（ADR-0021 §4-1）"];
        }

        var match = EnforcementSection.Match(editorConfig.ReplaceLineEndings("\n"));
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

    /// <summary>
    /// <c>.editorconfig</c> を Roslyn に解釈させ、規則の<b>実効の severity</b> を突き合わせる。
    /// </summary>
    /// <param name="editorConfigs">リポジトリ内のすべての <c>.editorconfig</c>（浅い順）。</param>
    /// <param name="projectsDirectory">プロジェクトを置いてあるディレクトリ（絶対パス）。</param>
    public static IReadOnlyList<string> EffectiveSeverityProblems(
        IEnumerable<(string Path, string Text)> editorConfigs, string projectsDirectory)
    {
        ArgumentNullException.ThrowIfNull(editorConfigs);

        var configs = editorConfigs.Select(file => AnalyzerConfig.Parse(file.Text, file.Path)).ToImmutableArray();
        if (configs.Length == 0)
        {
            return [".editorconfig が無い。記法の強制はこのファイルが土台である（ADR-0021 §4-1）"];
        }

        var set = AnalyzerConfigSet.Create(configs);
        var problems = new List<string>();

        foreach (var project in EnforcedProjects)
        {
            var options = set.GetOptionsForSourcePath(Path.Combine(projectsDirectory, project, "Probe.cs"));

            problems.AddRange(MustBeWarnings
                .Where(rule => Severity(options, rule) != ReportDiagnostic.Warn)
                .Select(rule => $"{project}: {rule} がビルドで警告にならない"
                              + $"（実効 {Severity(options, rule)}。ADR-0021 §4-1）"));

            problems.AddRange(MustNotBeWarnings
                .Where(rule => Severity(options, rule) == ReportDiagnostic.Warn)
                .Select(rule => $"{project}: {rule} は強制しないと決めた規則なのに警告になっている（ADR-0021 §4-1）"));

            // **severity は「報告するか」しか決めない。** 何を正とするかは option が決める。
            problems.AddRange(RuleOptions
                .Where(pair => Option(options, pair.Option) != pair.Value)
                .Select(pair => $"{project}: {pair.Option} が {pair.Value} でない"
                              + $"（実効 {Option(options, pair.Option) ?? "（未設定）"}）。"
                              + $"{pair.Rule} は警告のまま逆のことを言う（ADR-0021 §4-1）"));

            var warned = options.TreeOptions.Count(entry => entry.Value == ReportDiagnostic.Warn);
            if (warned < MinimumWarnRules)
            {
                problems.Add($"{project}: 警告になっている規則が {warned} 件しかない"
                           + $"（下限 {MinimumWarnRules}）。黙って外していないか（ADR-0021 §4-1）");
            }
        }

        // 対象外の側も見る。**「効いていること」と「効いていないこと」は別々に壊れる。**
        foreach (var project in TemplateDerivedProjects)
        {
            var options = set.GetOptionsForSourcePath(Path.Combine(projectsDirectory, project, "Probe.cs"));
            var warned = options.TreeOptions.Count(entry => entry.Value == ReportDiagnostic.Warn);
            if (warned != 0)
            {
                problems.Add($"{project}: CLB テンプレート由来なのに {warned} 件の規則が警告になっている（ADR-0021 §3）");
            }
        }

        return [.. problems.Order(StringComparer.Ordinal)];

        static ReportDiagnostic Severity(AnalyzerConfigOptionsResult options, string rule)
            => options.TreeOptions.TryGetValue(rule, out var severity) ? severity : ReportDiagnostic.Default;

        static string? Option(AnalyzerConfigOptionsResult options, string name)
            => options.AnalyzerOptions.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>どちらの表にも載っていないプロジェクト。</summary>
    /// <remarks>
    /// <b>実体はソリューションである。</b> ビルドされるのはソリューションに載っているもので
    /// あって、フォルダの有無ではない。ディレクトリを数えると、入れ子に置いたプロジェクトや
    /// <c>BusinessApp/</c> の外に置いたプロジェクトが黙って検査を外れる。
    /// </remarks>
    public static IReadOnlyList<string> UnlistedProjects(IEnumerable<(string Name, string Path)> solutionProjects)
    {
        ArgumentNullException.ThrowIfNull(solutionProjects);
        var listed = EnforcedProjects.Concat(TemplateDerivedProjects).ToHashSet(StringComparer.Ordinal);

        return
        [
            .. solutionProjects
                .Where(project => !listed.Contains(project.Name))
                .Select(project => $"{project.Name}（{project.Path}）がどちらの表にも載っていない（ADR-0021 §3 のどちらかに足す）")
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>改行が CRLF になっている C#。</summary>
    public static IReadOnlyList<string> CarriageReturnProblems(IEnumerable<(string Path, string Source)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return
        [
            .. files
                .Where(file => file.Source.Contains('\r'))
                .Select(file => $"{file.Path}: 改行が CRLF になっている（LF に直す。ADR-0021 §4-3）")
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary><c>.gitattributes</c> が C# の改行を LF に固定しているか。</summary>
    /// <remarks>
    /// <b>「その行が在るか」では足りない。</b> Git の属性は後勝ちで、
    /// しかも下の階層の <c>.gitattributes</c> が優先される。**打ち消しは、消すのと結果が同じで、
    /// 字面は残るぶん更に気づけない**（qa/02 R8-28）。
    /// そこで①ファイルが 1 本だけであること②その中の最後の指定が <c>lf</c> であることを見る。
    /// 属性の意味論を再現するより、<b>打ち消せない形に保つ</b>ほうが確かで安い。
    /// </remarks>
    public static IReadOnlyList<string> GitAttributesProblems(IEnumerable<(string Path, string Text)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var all = files.ToList();

        if (all.Count == 0)
        {
            return [".gitattributes が無い（ADR-0021 §4-3）"];
        }

        if (all.Count > 1)
        {
            return
            [
                $".gitattributes が {all.Count} 本ある（{string.Join(" / ", all.Select(file => file.Path))}）。"
                + "下の階層のものが優先されるので、改行の固定を黙って打ち消せる。1 本に保つ（ADR-0021 §4-3）",
            ];
        }

        var declarations = CSharpEolRule.Matches(all[0].Text.ReplaceLineEndings("\n"));
        if (declarations.Count == 0)
        {
            return
            [
                ".gitattributes に `*.cs text eol=lf` が無い。"
                + "raw string literal の改行はソースの改行そのものなので、"
                + "clone した機ごとに文言の改行コードが変わる（ADR-0021 §4-3）",
            ];
        }

        var effective = declarations[^1].Groups["eol"].Value;
        return effective == "lf"
            ? []
            : [$".gitattributes の C# の改行が最後に eol={effective} で上書きされている（ADR-0021 §4-3）"];
    }

    /// <summary>ソリューションに載っているプロジェクト。</summary>
    public static IReadOnlyList<(string Name, string Path)> SolutionProjectsIn(string solution)
        => [
            .. XDocument.Parse(solution)
                .Descendants("Project")
                .Select(element => element.Attribute("Path")?.Value)
                .Where(path => !string.IsNullOrEmpty(path))
                .Select(path => (Name: System.IO.Path.GetFileNameWithoutExtension(path)!, Path: path!))
                .OrderBy(project => project.Name, StringComparer.Ordinal),
        ];

    // -----------------------------------------------------------------------
    // 置き場所から範囲を決める
    // -----------------------------------------------------------------------

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
    /// <b>拾うのは <c>*.cs</c> だけである。</b> <c>*.razor</c> の中の C# は対象外（qa/02 R8-19）。
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

    // -----------------------------------------------------------------------
    // 構文上の判定
    // -----------------------------------------------------------------------

    /// <summary>型も位置パターンも無く、中身が空のプロパティパターン（＝<c>is { }</c>）。</summary>
    private static bool IsEmptyPropertyPattern(RecursivePatternSyntax pattern)
        => pattern.Type is null
           && pattern.PositionalPatternClause is null
           && pattern.PropertyPatternClause is not null
           && pattern.PropertyPatternClause.Subpatterns.Count == 0;

    /// <summary><c>x == null</c> / <c>x != null</c>（左右どちらでも、括弧とキャストで包んでも）。</summary>
    /// <remarks>
    /// <c>x == default</c> は見ない。<c>TaxCategoryId == default</c> のように
    /// <b>値型どうしの正しい比較が実在する</b>ので、意味解析なしでは区別できない（qa/02 R8-14）。
    /// </remarks>
    private static bool IsNullComparison(BinaryExpressionSyntax expression)
        => (expression.IsKind(SyntaxKind.EqualsExpression) || expression.IsKind(SyntaxKind.NotEqualsExpression))
           && (IsNullLiteral(expression.Left) || IsNullLiteral(expression.Right));

    private static bool IsNullLiteral(ExpressionSyntax expression)
        => Unwrap(expression) is LiteralExpressionSyntax literal
           && literal.IsKind(SyntaxKind.NullLiteralExpression);

    /// <summary>括弧とキャストを剥がす。<c>x == (object)null</c> は多重定義を迂回する常套手段である。</summary>
    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        => expression switch
        {
            ParenthesizedExpressionSyntax parenthesized => Unwrap(parenthesized.Expression),
            CastExpressionSyntax cast => Unwrap(cast.Expression),
            PostfixUnaryExpressionSyntax suppression
                when suppression.IsKind(SyntaxKind.SuppressNullableWarningExpression)
                => Unwrap(suppression.Operand),
            _ => expression,
        };

    private static bool IsConditionalDirective(SyntaxTrivia trivia)
        => trivia.IsKind(SyntaxKind.IfDirectiveTrivia)
           || trivia.IsKind(SyntaxKind.ElifDirectiveTrivia)
           || trivia.IsKind(SyntaxKind.ElseDirectiveTrivia);

    /// <summary>
    /// <c>Environment.NewLine</c> を、書き方を変えても捕まえる。
    /// </summary>
    /// <remarks>
    /// 字面で <c>"Environment"</c> と比べるだけだと、<c>global::System.Environment.NewLine</c>・
    /// <c>using static System.Environment;</c> のあとの裸の <c>NewLine</c>・
    /// <c>using Env = System.Environment;</c> の <c>Env.NewLine</c> がすべて抜ける（qa/02 R8-16）。
    /// <b>この層は狭いので、<c>NewLine</c> という名前そのものを疑ってよい。</b>
    /// <c>TextWriter.NewLine</c> のような無関係なメンバも当たる（qa/02 R8-32）。
    /// 赤が出たら誤検知を疑い、<c>#pragma</c> ではなく**書き方を変えて**避ける。
    /// </remarks>
    private static IEnumerable<SyntaxNode> EnvironmentNewLineUsages(SyntaxNode root)
        => root.DescendantNodes()
            .Where(node => node switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText == "NewLine",
                IdentifierNameSyntax identifier =>
                    identifier.Identifier.ValueText == "NewLine"
                    && identifier.Parent is not MemberAccessExpressionSyntax
                    && identifier.Parent is not UsingDirectiveSyntax
                    && identifier.Parent is not QualifiedNameSyntax,
                _ => false,
            });

    /// <summary>リテラルに CR が入っている。</summary>
    /// <remarks>
    /// 通常の文字列だけを見ていると、<b>補間文字列・文字リテラル・UTF-8 リテラルがすり抜ける</b>
    /// （qa/02 R8-31）。ファイルの実バイトを見る検査にも当たらないので、ここが唯一の網である。
    /// raw string literal は値の段階で CRLF が LF に正規化されるので、実バイト側の検査に任せる。
    /// </remarks>
    private static IEnumerable<SyntaxNode> CarriageReturnLiterals(SyntaxNode root)
        => root.DescendantNodes().Where(node => node switch
        {
            LiteralExpressionSyntax literal =>
                (literal.IsKind(SyntaxKind.StringLiteralExpression)
                 || literal.IsKind(SyntaxKind.Utf8StringLiteralExpression)
                 || literal.IsKind(SyntaxKind.CharacterLiteralExpression))
                && literal.Token.ValueText.Contains('\r'),
            InterpolatedStringTextSyntax text => text.TextToken.ValueText.Contains('\r'),
            _ => false,
        });

    private static int Line(SyntaxNode node)
        => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int Line(SyntaxTrivia trivia)
        => trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string? ReadIfExists(string path)
        => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>規則を抑制しているプロパティ。</summary>
    private static IEnumerable<string> Suppressions(
        string project, IEnumerable<XElement> properties, string where)
        => from element in properties
           where SuppressingProperties.Contains(element.Name.LocalName, StringComparer.Ordinal)
           from id in element.Value.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries)
           where id.StartsWith("IDE", StringComparison.Ordinal)
           select $"{project}: {where} の <{element.Name.LocalName}> が {id} を抑制している。"
                + "関門が死ぬので、抑制するなら #pragma で 1 か所ずつ（ADR-0021 §4-1）";

    /// <summary>Condition の有無によらず、すべての PropertyGroup の中身。</summary>
    private static IReadOnlyList<XElement> AllProperties(string xml)
        => [.. XDocument.Parse(xml).Descendants("PropertyGroup").SelectMany(group => group.Elements())];

    /// <summary>
    /// 走査から外す場所。
    /// </summary>
    /// <remarks>
    /// <para><c>.claude/worktrees/</c> は <b>このリポジトリの別のチェックアウト</b>である
    /// （<c>git worktree</c>）。入れると同じソースを 2 回数えることになり、
    /// <c>.gitattributes</c> は「2 本ある」、ソースは 2 倍の件数として見える。
    /// <b>関門が、作業のやり方（ワークツリーを使ったかどうか）で結果を変えてはいけない。</b></para>
    /// <para>2026-08-26 に実際に鳴った——前のセッションが残したワークツリーがあるだけで、
    /// 改行の固定の検査が落ちた。</para>
    /// </remarks>
    public static bool IsExcluded(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (segments is [".claude", "worktrees", ..])
        {
            return true;
        }

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
