namespace BusinessApp.TestSupport;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// C# の記法規約（ADR-0021）のうち、<b>アナライザで表現できないもの</b>を検査する。
/// </summary>
/// <remarks>
/// <para><c>.editorconfig</c> ＋ <c>EnforceCodeStyleInBuild</c> でビルドに載せられるのは
/// Roslyn が診断を持っているルールだけである。<c>is { }</c> の禁止のように
/// 「書ける形のうち特定のものを使わない」という規約には診断が無いので、
/// <b>ソースを読んで検査する</b>。</para>
/// <para><b>判定は返り値で返し、ここでは表明しない。</b>
/// <see cref="TestLayoutConvention"/> と同じ作法（テストの書き方に縛られない）。</para>
/// </remarks>
public sealed class CSharpStyleConvention(string repositoryRoot)
{
    /// <summary>記法規約をビルドで強制するプロジェクト（ADR-0021 §4）。</summary>
    /// <remarks>
    /// ここに載っているプロジェクトは <c>TreatWarningsAsErrors</c> と
    /// <c>EnforceCodeStyleInBuild</c> の両方を宣言していなければならない。
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
    /// <b>ただし <see cref="EverywhereForms"/> の禁止形だけは、ここにも効く。</b>
    /// </remarks>
    public static readonly string[] TemplateDerivedProjects =
    [
        "BusinessApp.Client",
        "BusinessApp.Client.Shared",
        "BusinessApp.Designer",
        "BusinessApp.LicenseRegisterCli",
        "BusinessApp.Server",
    ];

    /// <summary>リポジトリ内のすべての C# に効く禁止形。</summary>
    /// <remarks>
    /// CLB スクリプト（<c>*.mod.cs</c>）も含む。読み手のコストの問題なので、
    /// 書かれている場所によらず禁じてよい。
    /// </remarks>
    private static readonly ForbiddenForm[] EverywhereForms =
    [
        new(@"is\s+(not\s+)?\{\s*\}",
            "is { } / is not { }",
            "null 検査であることが字面から読めない。型パターン（is int v）を使う（ADR-0021 §2）"),
    ];

    /// <summary>記法を強制するプロジェクトにだけ効く禁止形。</summary>
    private static readonly ForbiddenForm[] EnforcedProjectForms =
    [
        new(@"[=!]=\s*null\b",
            "== null / != null",
            "演算子の多重定義に左右される。is null / is not null を使う（ADR-0021 §2）"),
    ];

    /// <summary>利用者に見せる文言を組み立てる層にだけ効く禁止形。</summary>
    /// <remarks>
    /// raw string literal の改行はソースの改行（＝LF。<c>.gitattributes</c> で固定）であり、
    /// <c>Environment.NewLine</c> は Windows では CRLF である。
    /// 混ぜると<b>ひとつの文言の中で改行コードが割れる</b>（ADR-0021 §2）。
    /// テストの表明メッセージは利用者に見えないので、この禁止は掛けない。
    /// </remarks>
    private static readonly (string Project, ForbiddenForm Form)[] MessageLayerForms =
    [
        ("BusinessApp.AccountingCore", NewLineForm()),
        ("BusinessApp.AccountingCore.Server", NewLineForm()),
    ];

    public string RepositoryRoot { get; } = repositoryRoot;

    /// <summary>プロジェクトを置いてあるディレクトリ。</summary>
    public string ProjectsDirectory => Path.Combine(RepositoryRoot, "BusinessApp");

    /// <summary>禁止形を使っている箇所。</summary>
    public IReadOnlyList<string> ForbiddenFormUsages()
    {
        var violations = new List<string>();

        foreach (var file in SourceFiles(RepositoryRoot))
        {
            violations.AddRange(UsagesIn(file, EverywhereForms));
        }

        foreach (var project in EnforcedProjects)
        {
            foreach (var file in SourceFiles(Path.Combine(ProjectsDirectory, project)))
            {
                violations.AddRange(UsagesIn(file, EnforcedProjectForms));
            }
        }

        foreach (var (project, form) in MessageLayerForms)
        {
            foreach (var file in SourceFiles(Path.Combine(ProjectsDirectory, project)))
            {
                violations.AddRange(UsagesIn(file, [form]));
            }
        }

        return violations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>記法の強制を宣言していないプロジェクト。</summary>
    public IReadOnlyList<string> ProjectsWithoutEnforcement()
        => EnforcedProjects
            .Select(name => (Name: name, File: Path.Combine(ProjectsDirectory, name, name + ".csproj")))
            .SelectMany(project => MissingProperties(project.Name, project.File))
            .ToList();

    /// <summary>どちらの表にも載っていないプロジェクト（＝黙って検査の外にいるもの）。</summary>
    /// <remarks>
    /// プロジェクトを増やしたら必ずどちらかの表に足すことになる。
    /// <b>表を持たないと「ここは例外」が既定になる。</b>
    /// </remarks>
    public IReadOnlyList<string> ProjectsMissingFromTable()
    {
        var listed = EnforcedProjects.Concat(TemplateDerivedProjects).ToHashSet(StringComparer.Ordinal);

        return Directory.EnumerateDirectories(ProjectsDirectory)
            .Where(directory => Directory.EnumerateFiles(directory, "*.csproj").Any())
            .Select(directory => Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)))
            .Where(name => !listed.Contains(name))
            .Select(name => $"{name} がどちらの表にも載っていない（ADR-0021 §3 のどちらかに足す）")
            .Order(StringComparer.Ordinal)
            .ToList();
    }

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

    /// <summary>
    /// コメントと文字列リテラルを取り除く。<b>検査をコードにだけ効かせるため。</b>
    /// </summary>
    /// <remarks>
    /// <para>規約そのものを説明する doc コメント（「<c>is { }</c> は使わない」）や、
    /// SQL の中の文字列まで違反に数えると、規約を書けなくなる。</para>
    /// <para><b>C# の字句解析を厳密に再現しなくてよい。</b> 補間文字列の穴（<c>{...}</c>）の中は
    /// コードだが、ここでは文字列の一部として捨てる。取りこぼしても、落ちたときに人が読んで直せる
    /// 粒度であればよい。</para>
    /// <para>取り除いた分は同じ長さの空白に置き換える。<b>行番号と桁がずれると報告が使えなくなる</b>ため。</para>
    /// </remarks>
    public static string StripCommentsAndLiterals(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var stripped = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var consumed = SkipComment(source, index, stripped)
                ?? SkipRawString(source, index, stripped)
                ?? SkipVerbatimString(source, index, stripped)
                ?? SkipQuoted(source, index, stripped);

            if (consumed is int next)
            {
                index = next;
                continue;
            }

            stripped.Append(source[index]);
            index++;
        }

        return stripped.ToString();
    }

    private static ForbiddenForm NewLineForm()
        => new(@"\bEnvironment\.NewLine\b",
            "Environment.NewLine",
            "利用者に見せる文言の改行は raw string literal の改行（LF）に統一する（ADR-0021 §2）");

    private static IEnumerable<string> UsagesIn(string file, IReadOnlyList<ForbiddenForm> forms)
    {
        var lines = StripCommentsAndLiterals(File.ReadAllText(file)).Split('\n');

        return from form in forms
               from line in lines.Select((text, number) => (text, number))
               where form.Pattern.IsMatch(line.text)
               select $"{file}({line.number + 1}): {form.Name} は使わない — {form.Reason}";
    }

    private static IEnumerable<string> MissingProperties(string project, string csproj)
    {
        if (!File.Exists(csproj))
        {
            yield return $"{project}: {Path.GetFileName(csproj)} が見つからない";
            yield break;
        }

        var text = File.ReadAllText(csproj);
        string[] required = ["TreatWarningsAsErrors", "EnforceCodeStyleInBuild"];
        foreach (var property in required)
        {
            if (!text.Contains($"<{property}>true</{property}>", StringComparison.Ordinal))
            {
                yield return $"{project}: <{property}>true</{property}> が無い（ADR-0021 §4）";
            }
        }
    }

    private static bool IsExcluded(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment =>
            segment is "bin" or "obj" or ".vs" or ".git" or "StrykerOutput" or "TestResults" or "ClaudeCodeForDesigner");
    }

    /// <summary>行コメント・ブロックコメントを飛ばす。飛ばさなかったときは null。</summary>
    private static int? SkipComment(string source, int index, StringBuilder stripped)
    {
        if (!Matches(source, index, "//") && !Matches(source, index, "/*"))
        {
            return null;
        }

        var end = source[index + 1] == '/'
            ? IndexOrEnd(source, source.IndexOf('\n', index))
            : IndexOrEnd(source, source.IndexOf("*/", index, StringComparison.Ordinal), "*/".Length);

        Blank(source, index, end, stripped);
        return end;
    }

    /// <summary>raw string literal（<c>"""</c> 以上）を飛ばす。</summary>
    private static int? SkipRawString(string source, int index, StringBuilder stripped)
    {
        var start = SkipInterpolationPrefix(source, index);
        var quotes = CountQuotes(source, start);
        if (quotes < 3)
        {
            return null;
        }

        var closing = new string('"', quotes);
        var end = IndexOrEnd(source, source.IndexOf(closing, start + quotes, StringComparison.Ordinal), quotes);

        Blank(source, index, end, stripped);
        return end;
    }

    /// <summary>逐語的文字列（<c>@"..."</c>）を飛ばす。</summary>
    private static int? SkipVerbatimString(string source, int index, StringBuilder stripped)
    {
        var start = SkipInterpolationPrefix(source, index);
        if (start >= source.Length || source[start] != '@')
        {
            return null;
        }

        start = SkipInterpolationPrefix(source, start + 1);
        if (start >= source.Length || source[start] != '"')
        {
            return null;
        }

        var cursor = start + 1;
        while (cursor < source.Length)
        {
            if (source[cursor] != '"')
            {
                cursor++;
            }
            else if (Matches(source, cursor, "\"\""))
            {
                cursor += 2;   // 逐語的文字列の中の "" は 1 個の " を表す
            }
            else
            {
                cursor++;
                break;
            }
        }

        Blank(source, index, cursor, stripped);
        return cursor;
    }

    /// <summary>通常の文字列リテラルと文字リテラルを飛ばす。</summary>
    private static int? SkipQuoted(string source, int index, StringBuilder stripped)
    {
        var start = SkipInterpolationPrefix(source, index);
        if (start >= source.Length || source[start] is not ('"' or '\''))
        {
            return null;
        }

        var quote = source[start];
        var cursor = start + 1;
        while (cursor < source.Length && source[cursor] != quote)
        {
            // 改行で閉じていないリテラルは、閉じ忘れではなく走査のずれである。行で打ち切る。
            if (source[cursor] == '\n')
            {
                break;
            }

            cursor += source[cursor] == '\\' ? 2 : 1;
        }

        var end = Math.Min(cursor + 1, source.Length);
        Blank(source, index, end, stripped);
        return end;
    }

    /// <summary>補間の <c>$</c>（<c>$$</c> もある）を飛ばした位置。</summary>
    private static int SkipInterpolationPrefix(string source, int index)
    {
        while (index < source.Length && source[index] == '$')
        {
            index++;
        }

        return index;
    }

    private static int CountQuotes(string source, int index)
    {
        var count = 0;
        while (index + count < source.Length && source[index + count] == '"')
        {
            count++;
        }

        return count;
    }

    private static bool Matches(string source, int index, string token)
        => index + token.Length <= source.Length
           && source.AsSpan(index, token.Length).SequenceEqual(token);

    private static int IndexOrEnd(string source, int found, int width = 0)
        => found < 0 ? source.Length : Math.Min(found + width, source.Length);

    /// <summary>取り除いた範囲を、改行だけ残して空白に置き換える（行番号を保つ）。</summary>
    private static void Blank(string source, int from, int to, StringBuilder stripped)
    {
        for (var i = from; i < to; i++)
        {
            stripped.Append(source[i] == '\n' ? '\n' : ' ');
        }
    }

    /// <param name="Pattern">禁止形を見つける正規表現。</param>
    /// <param name="Name">報告に出す名前。</param>
    /// <param name="Reason">なぜ使わないか。<b>直し方まで書く。</b></param>
    private sealed record ForbiddenForm(Regex Pattern, string Name, string Reason)
    {
        public ForbiddenForm(string pattern, string name, string reason)
            : this(new Regex(pattern, RegexOptions.CultureInvariant), name, reason)
        {
        }
    }
}
