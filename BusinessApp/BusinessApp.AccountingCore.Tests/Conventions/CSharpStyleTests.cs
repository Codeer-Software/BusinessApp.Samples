namespace BusinessApp.AccountingCore.Tests.Conventions;

/// <summary>
/// C# の記法規約（ADR-0021）。判定は <see cref="CSharpStyleConvention"/> が持つ。
/// </summary>
/// <remarks>
/// <para><b>ここが見るのは、アナライザで表現できないものだけ</b>である。
/// <c>.editorconfig</c> で表現できるものは <c>EnforceCodeStyleInBuild</c> がビルドで止める
/// （<c>TreatWarningsAsErrors</c> と組でエラーになる）。</para>
/// <para><b>「違反 0 件」を表明するだけのテストは、検査そのものが壊れても緑になる。</b>
/// 実際、最初はそうなっていた——判定の中身を空にしても 6 つのうち 5 つは落ちなかった
/// （qa/02 R8-11）。そこで<b>すべての検査に「壊した入力を食わせて赤になる」対</b>を置いてある。
/// 通す経路は本番と同じ関数である（テストだけが通る別の道を作らない）。</para>
/// <para>リポジトリ全体を見るのでこのプロジェクトの外まで検査するが、
/// このためにテストプロジェクトを 1 つ増やすほうが高くつくのでここに置く。</para>
/// </remarks>
public class CSharpStyleTests
{
    private static readonly CSharpStyleConvention Convention =
        new(CSharpStyleConvention.FindRepositoryRoot());

    // =====================================================================
    // 1. 現状が規約を満たしていること
    // =====================================================================

    [Fact]
    public void 禁止された記法を使っていない()
        => AssertNone(Convention.ForbiddenFormUsages());

    [Fact]
    public void 対象プロジェクトは記法の強制を宣言している()
        => AssertNone(Convention.ProjectsWithoutEnforcement());

    /// <summary>
    /// 強制対象を黙って減らせない。
    /// </summary>
    /// <remarks>
    /// 3 つの表（csproj・<c>.editorconfig</c>・<c>EnforcedProjects</c>）は互いだけを照合しているので、
    /// <b>3 か所を揃えて動かすと、整合したまま関門だけが消える</b>（qa/02 R8-12）。
    /// ミューテーションスコアと同じ作法で下限を置く。<b>下げるときは理由を書いて下げる。</b>
    /// </remarks>
    [Fact]
    public void 強制対象を黙って減らせない()
        => Assert.True(
            CSharpStyleConvention.EnforcedProjects.Length >= CSharpStyleConvention.MinimumEnforcedProjects,
            $"強制対象が {CSharpStyleConvention.EnforcedProjects.Length} 件（下限 "
            + $"{CSharpStyleConvention.MinimumEnforcedProjects}）。黙って対象外へ降格していないか（ADR-0021 §4-1）");

    [Fact]
    public void すべてのプロジェクトがどちらかの表に載っている()
        => AssertNone(Convention.ProjectsMissingFromTable());

    [Fact]
    public void editorconfig_の強制セクションと表が一致している()
        => AssertNone(Convention.EditorConfigMismatches());

    /// <summary>
    /// 規則がビルドで実際に警告になる。
    /// </summary>
    /// <remarks>
    /// <b>見出しの照合だけでは、中身の severity 行が全部消えても緑になる</b>（qa/02 R8-10）。
    /// ここは Roslyn に <c>.editorconfig</c> を解釈させて<b>実効の severity</b> を引く。
    /// 対象外のプロジェクトで警告に<b>ならない</b>ことも同時に見る。
    /// </remarks>
    [Fact]
    public void 規則はビルドで実際に警告になる()
        => AssertNone(Convention.EffectiveSeverityProblems());

    /// <summary>改行が LF に固定されていて、実際に LF である。</summary>
    [Fact]
    public void ソースの改行は_LF_に固定されている()
        => AssertNone(Convention.LineEndingProblems());

    /// <summary>
    /// 検査が実際にソースを読んでいる。
    /// </summary>
    /// <remarks>
    /// 場所の解決を間違えると、<b>1 つも読まないまま「違反 0 件」で緑になる</b>。
    /// 「違反が無い」と「何も見ていない」は、結果の見た目が同じである。
    /// </remarks>
    [Fact]
    public void リポジトリのルートを見つけて実際にソースを読んでいる()
    {
        Assert.True(Directory.Exists(Convention.ProjectsDirectory), "BusinessApp/ が見つからない");
        Assert.True(File.Exists(Convention.SolutionFile), "BusinessApp.slnx が見つからない");
        Assert.True(File.Exists(Convention.EditorConfigFile), ".editorconfig が見つからない");
        Assert.True(File.Exists(Convention.GitAttributesFile), ".gitattributes が見つからない");

        // 表に書いた名前がソリューションに実在すること。
        var actual = Convention.SolutionProjects().Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var missing = CSharpStyleConvention.EnforcedProjects
            .Concat(CSharpStyleConvention.TemplateDerivedProjects)
            .Where(project => !actual.Contains(project))
            .ToList();
        Assert.True(missing.Count == 0, $"表にあるがソリューションに無い: {string.Join(" / ", missing)}");

        // 実際に読めている証拠として、この検査自身のソースを見つけられること。
        var files = CSharpStyleConvention.SourceFiles(Convention.RepositoryRoot).ToList();
        Assert.Contains(files, path => path.EndsWith("CSharpStyleTests.cs", StringComparison.Ordinal));
        Assert.True(files.Count > 100, $"読めた C# が {files.Count} 本しかない（除外の指定が広すぎないか）");

        // CLB スクリプト（*.mod.cs）も検査の対象である（ADR-0021 §2 の禁止形は場所によらない）。
        Assert.Contains(files, CSharpStyleConvention.IsClbScript);
    }

    // =====================================================================
    // 2. 禁止形の判定（わざと壊して鳴るか）
    // =====================================================================

    /// <summary>
    /// 空のプロパティパターンを、書き方を変えても取りこぼさない。
    /// </summary>
    /// <remarks>
    /// <b>最初は正規表現で書いていて、ここに挙げた形の半分を取りこぼしていた</b>（qa/02 R8-01）。
    /// C# は <c>is</c> と中かっこの間に空白を要求しないし、パターンは改行をまたげる。
    /// </remarks>
    [Theory]
    [InlineData("if (x is { } y) { }")]
    [InlineData("if (x is not { } y) { }")]
    [InlineData("if (x is{}) { }")]                             // 空白なし
    [InlineData("if (x is  {   } y) { }")]                      // 空白が複数
    [InlineData("if (x is not{} ) { }")]                        // not の後ろも空白なし
    [InlineData("if (x is not (not { })) { }")]                 // 二重否定
    [InlineData("var v = x switch { { } y => 1, _ => 0 };")]    // switch 式の中
    [InlineData("var v = x is [{ }];")]                         // リストパターンの中
    [InlineData("var v = x is { P: { } };")]                    // 入れ子
    public void 禁止形は書き方を変えても捕まる(string source)
        => Assert.NotEmpty(Find(source));

    [Fact]
    public void 禁止形は改行をまたいでも捕まる()
    {
        var source = """
            if (x is
                { } y)
            {
            }
            """;

        var found = Find(source);

        // 報告する行は、中かっこが書かれた 2 行目（`is` の行ではない）。
        // 直す場所そのものを指すほうが読み手には近い。
        Assert.Single(found);
        Assert.Equal(2, found[0].Line);
    }

    /// <summary>
    /// 規約そのものを説明する文と、似て非なる正しい記法を、違反に数えない。
    /// </summary>
    /// <remarks>
    /// <b>誤検知は「規約を書けなくなる」形で効いてくる。</b>
    /// doc コメントに例を書けなければ、なぜ使わないかを説明できない。
    /// </remarks>
    [Theory]
    [InlineData("// x is { } y")]
    [InlineData("/// <c>is { }</c> は使わない")]
    [InlineData("/* a is { } b */ var x = 1;")]
    [InlineData("var sql = \"where a is { }\";")]
    [InlineData("var v = @\"is { }\";")]
    [InlineData("var v = @\"a\"\"b is { }\";")]                  // 逐語的文字列の中の ""
    [InlineData("var url = \"https://example.test/is { }\";")]   // 文字列の中の // でコメントが始まらない
    [InlineData("var v = $\"{s.Split('\"')[0]} is OK\";")]       // 補間の穴の中の引用符
    [InlineData("var v = $\"{s.Replace(\"a\", \"b\")} ok\";")]   // 補間の穴の中の文字列
    [InlineData("var c = '\"';")]                                // 文字リテラルの中の引用符
    [InlineData("if (x is string { }) { }")]                     // 型があるので別の意味
    [InlineData("if (x is { Length: 0 }) { }")]                  // 中身があるので別の意味
    [InlineData("if (x is not null) { }")]                       // 正しい記法
    [InlineData("if (x is int v) { }")]                          // 正しい記法
    [InlineData("if (x is var v) { }")]                          // 正しい記法
    public void 規約の説明と正しい記法は違反にしない(string source)
        => Assert.Empty(Find(source));

    [Fact]
    public void raw_string_literal_の中は違反にしない()
    {
        var source = """"
            var sql = """
                select * from t where a is { }
                """;
            """";

        Assert.Empty(Find(source));
    }

    /// <summary>
    /// null の比較は、括弧・キャスト・Yoda 形で包んでも捕まる。
    /// </summary>
    /// <remarks>
    /// <c>x == (object)null</c> は<b>演算子の多重定義を迂回するために人が書く形</b>であり、
    /// ADR-0021 §2 がこの規則を置いた理由そのものである（qa/02 R8-14）。
    /// </remarks>
    [Theory]
    [InlineData("if (x == null) { }", true)]
    [InlineData("if (x != null) { }", true)]
    [InlineData("if (null == x) { }", true)]
    [InlineData("if (null != x) { }", true)]
    [InlineData("if ((x) == (null)) { }", true)]
    [InlineData("if (x == (object)null) { }", true)]
    [InlineData("if (x is null) { }", false)]
    [InlineData("if (x is not null) { }", false)]
    [InlineData("if (x == default) { }", false)]                 // 値型の正しい比較が実在する
    [InlineData("var s = \"a == null\";", false)]
    [InlineData("// x == null", false)]
    public void null_の比較は演算子ではなくパターンで書く(string source, bool caught)
        => Assert.Equal(caught, Find(source, ForbiddenFormSet.EnforcedProject).Count > 0);

    /// <summary>
    /// 条件付きコンパイルを使わない。
    /// </summary>
    /// <remarks>
    /// <b><c>#if</c> の中は構文木から消える</b>ので、そこに書かれた禁止形は検査を素通りし、
    /// 「違反 0 件」と見分けが付かない（qa/02 R8-17）。分岐の網羅という難問を持ち込むより、
    /// 条件付きコンパイルそのものを使わない。
    /// </remarks>
    [Fact]
    public void 条件付きコンパイルは使わない()
    {
        var source = """
            #if DEBUG
            var x = 1;
            #endif
            """;

        Assert.NotEmpty(Find(source, ForbiddenFormSet.EnforcedProject));
    }

    [Theory]
    [InlineData("var s = Environment.NewLine;", true)]
    [InlineData("var s = System.Environment.NewLine;", true)]
    [InlineData("var s = global::System.Environment.NewLine;", true)]
    [InlineData("var s = Env.NewLine;", true)]                    // using Env = System.Environment;
    [InlineData("var s = NewLine;", true)]                        // using static System.Environment;
    [InlineData("var s = \"改行なし\";", false)]
    public void 利用者向け文言の層では改行に_Environment_を使わない(string source, bool caught)
        => Assert.Equal(caught, Find(source, ForbiddenFormSet.MessageLayer).Count > 0);

    /// <summary>
    /// 文言の中に CR を書かない。
    /// </summary>
    /// <remarks>
    /// <c>Environment.NewLine</c> を禁じても、CR を含む文字列リテラルを書けば同じ結果になる
    /// （qa/02 R8-21）。ファイルの実バイトを見る CRLF 検査には、この形は当たらない。
    /// </remarks>
    [Fact]
    public void 文言の中に_CR_を書かない()
    {
        var carriageReturn = ((char)13).ToString();
        var source = $"var s = string.Join(\"{carriageReturn}\", lines);";

        Assert.NotEmpty(Find(source, ForbiddenFormSet.MessageLayer));
        Assert.Empty(Find("var s = string.Join(\"x\", lines);", ForbiddenFormSet.MessageLayer));
    }

    /// <summary>
    /// 読めなかったことを「違反 0 件」と混同しない。
    /// </summary>
    /// <remarks>
    /// 構文が壊れていればどの禁止形にも当たらず、見た目は「きれい」になる（qa/02 R8-18）。
    /// </remarks>
    [Fact]
    public void 構文を読めないファイルは違反として報告する()
    {
        var found = Find("class Broken { void M( { }");

        Assert.NotEmpty(found);
        Assert.Contains(found, entry => entry.Message.Contains("読めない", StringComparison.Ordinal));
    }

    /// <summary>
    /// 禁止形は範囲ごとに効く。<b>範囲の指定を間違えると、効かせたい層で何も見なくなる。</b>
    /// </summary>
    [Fact]
    public void 禁止形は指定した範囲でだけ効く()
    {
        const string NullComparison = "if (x == null) { }";
        const string NewLine = "var s = Environment.NewLine;";
        const string EmptyPattern = "if (x is { } y) { }";

        Assert.Empty(Find(NullComparison, ForbiddenFormSet.Everywhere));
        Assert.Empty(Find(NewLine, ForbiddenFormSet.Everywhere));
        Assert.Empty(Find(EmptyPattern, ForbiddenFormSet.EnforcedProject));
        Assert.Empty(Find(EmptyPattern, ForbiddenFormSet.None));

        Assert.NotEmpty(Find(NullComparison, ForbiddenFormSet.Everywhere | ForbiddenFormSet.EnforcedProject));
    }

    /// <summary>
    /// CLB スクリプト（<c>*.mod.cs</c>）も検査できる。
    /// </summary>
    /// <remarks>
    /// クラスの外にメソッドを書くので解析の種類が違う。
    /// <b>ここを間違えると、<c>Designer/</c> 配下だけが黙って素通りする。</b>
    /// </remarks>
    [Fact]
    public void CLB_スクリプトの形でも検査できる()
    {
        var source = """
            // 仕訳伝票の画面。
            void Detail_OnAfterInitialization()
            {
                if (Partner.Value is { } partner)
                {
                    Total.Value = 0;
                }
            }
            """;

        Assert.NotEmpty(Find(source, ForbiddenFormSet.Everywhere, isClbScript: true));
    }

    // =====================================================================
    // 3. 走査と範囲の配線（わざと壊して鳴るか）
    // =====================================================================

    /// <summary>
    /// 走査が、ファイルの置き場所に応じた禁止形を当てている。
    /// </summary>
    /// <remarks>
    /// <b>判定が正しくても、配線を間違えれば何も見ない。</b>
    /// ディスクを読む側にしか経路が無いと、ここが壊れても誰も気づかない（qa/02 R8-11）。
    /// </remarks>
    [Fact]
    public void 走査は置き場所に応じた禁止形を当てる()
    {
        var core = Path.Combine(Convention.ProjectsDirectory, "BusinessApp.AccountingCore", "X.cs");
        var template = Path.Combine(Convention.ProjectsDirectory, "BusinessApp.Server", "Y.cs");
        var script = Path.Combine(Convention.RepositoryRoot, "Designer", "Design", "Modules", "Z.mod.cs");

        var found = Convention.ForbiddenFormUsagesIn(
        [
            (core, "var a = Environment.NewLine; var b = x == null; var c = y is { } v;"),
            // `== null` はテンプレート由来のプロジェクトでは効かない（3 つ書いても 2 つしか出ない）。
            (template, "var a = Environment.NewLine; var b = x == null; var c = y is { } v;"),
            (script, "void M() { if (y is { } v) { } }"),
        ]);

        // 会計コアは 3 つとも効く。BusinessApp.Server は `is { }` と改行の 2 つ
        // （テンプレート由来だが文面を画面へ返す層なので MessageLayer に入っている）。
        // CLB スクリプトは `is { }` だけ。
        Assert.Equal(3, found.Count(message => message.StartsWith(core, StringComparison.Ordinal)));
        Assert.Equal(2, found.Count(message => message.StartsWith(template, StringComparison.Ordinal)));
        Assert.Equal(1, found.Count(message => message.StartsWith(script, StringComparison.Ordinal)));
    }

    [Fact]
    public void 走査は違反が無ければ何も報告しない()
        => Assert.Empty(Convention.ForbiddenFormUsagesIn(
            [(Path.Combine(Convention.ProjectsDirectory, "BusinessApp.AccountingCore", "X.cs"), "var a = 1;")]));

    [Theory]
    [InlineData(
        "BusinessApp.AccountingCore",
        ForbiddenFormSet.Everywhere | ForbiddenFormSet.EnforcedProject | ForbiddenFormSet.MessageLayer)]
    [InlineData(
        "BusinessApp.AccountingCore.Server",
        ForbiddenFormSet.Everywhere | ForbiddenFormSet.EnforcedProject | ForbiddenFormSet.MessageLayer)]
    [InlineData("BusinessApp.AccountingCore.Tests", ForbiddenFormSet.Everywhere | ForbiddenFormSet.EnforcedProject)]
    // BusinessApp.Server は CLB テンプレート由来だが、例外の文面を画面へ返す層でもある。
    [InlineData("BusinessApp.Server", ForbiddenFormSet.Everywhere | ForbiddenFormSet.MessageLayer)]
    [InlineData("BusinessApp.Client.Shared", ForbiddenFormSet.Everywhere)]
    public void プロジェクトごとに効く禁止形が決まる(string project, ForbiddenFormSet expected)
        => Assert.Equal(expected, Convention.SetsFor(Path.Combine(Convention.ProjectsDirectory, project, "Sample.cs")));

    [Fact]
    public void プロジェクトの外のソースにも共通の禁止形は効く()
    {
        var script = Path.Combine(Convention.RepositoryRoot, "Designer", "Design", "Modules", "X.mod.cs");

        Assert.Equal(ForbiddenFormSet.Everywhere, Convention.SetsFor(script));
        Assert.True(CSharpStyleConvention.IsClbScript(script));
    }

    // =====================================================================
    // 4. 関門そのものの検査（わざと壊して鳴るか）
    // =====================================================================

    /// <summary>
    /// csproj の宣言の検査は、宣言を消す・打ち消す形の両方で鳴る。
    /// </summary>
    /// <remarks>
    /// <b>「必要なプロパティが有る」ことは「関門が効いている」ことを意味しない。</b>
    /// <c>NoWarn</c> を 1 行足せば、宣言を残したまま関門を殺せる（qa/02 R8-13）。
    /// </remarks>
    [Theory]
    [InlineData(
        "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
        + "<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild></PropertyGroup></Project>",
        0)]
    [InlineData(
        "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>",
        1)]
    [InlineData("<Project><PropertyGroup /></Project>", 2)]
    [InlineData(
        "<Project><PropertyGroup><!-- <TreatWarningsAsErrors>true</TreatWarningsAsErrors> -->"
        + "<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild></PropertyGroup></Project>",
        1)]
    [InlineData(
        "<Project><PropertyGroup Condition=\"'$(Configuration)'=='Debug'\">"
        + "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
        + "<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild></PropertyGroup></Project>",
        2)]
    [InlineData(
        "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
        + "<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>"
        + "<NoWarn>IDE0055;IDE0161</NoWarn></PropertyGroup></Project>",
        2)]
    [InlineData(
        "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
        + "<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>"
        + "<WarningsNotAsErrors>IDE0055</WarningsNotAsErrors></PropertyGroup></Project>",
        1)]
    [InlineData(
        "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors>"
        + "<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>"
        + "<NoWarn>CS1591</NoWarn></PropertyGroup></Project>",
        0)]
    public void csproj_の宣言の検査は消す形も打ち消す形も鳴る(string csproj, int expected)
        => Assert.Equal(expected, CSharpStyleConvention.EnforcementProblems("Probe", csproj).Count);

    [Fact]
    public void csproj_が無ければ報告する()
        => Assert.Single(CSharpStyleConvention.EnforcementProblems("Probe", null));

    /// <summary><c>.editorconfig</c> の見出しの照合は、両方向で鳴る。</summary>
    [Fact]
    public void editorconfig_の見出しの照合は両方向で鳴る()
    {
        var all = string.Join(',', CSharpStyleConvention.EnforcedProjects.Select(Suffix));

        Assert.Empty(CSharpStyleConvention.EditorConfigMismatches(Section(all)));
        Assert.NotEmpty(CSharpStyleConvention.EditorConfigMismatches(Section(all.Replace("AccountingCore,", "", StringComparison.Ordinal))));
        Assert.NotEmpty(CSharpStyleConvention.EditorConfigMismatches(Section(all + ",Nonexistent")));
        Assert.NotEmpty(CSharpStyleConvention.EditorConfigMismatches("root = true"));
        Assert.NotEmpty(CSharpStyleConvention.EditorConfigMismatches(null));

        static string Suffix(string project) => project["BusinessApp.".Length..];
        static string Section(string names) => $"[BusinessApp/BusinessApp.{{{names}}}/**.cs]";
    }

    /// <summary>
    /// 実効 severity の検査は、severity 行が消えても・打ち消されても鳴る。
    /// </summary>
    /// <remarks>
    /// <b>これが今回いちばん危なかった穴である。</b> 見出しの照合だけを持っていたときは、
    /// <c>.editorconfig</c> の <c>dotnet_diagnostic</c> 行を全部消しても 44 件緑のままだった
    /// （qa/02 R8-10。レビュアが実測した）。
    /// </remarks>
    [Fact]
    public void 実効_severity_の検査は消しても打ち消しても鳴る()
    {
        var root = Convention.RepositoryRoot;
        var projects = Convention.ProjectsDirectory;
        var editorConfig = Path.Combine(root, ".editorconfig");
        var section = $"[BusinessApp/BusinessApp.{{{string.Join(',', CSharpStyleConvention.EnforcedProjects.Select(p => p["BusinessApp.".Length..]))}}}/**.cs]";
        var warnings = string.Join('\n', CSharpStyleConvention.MustBeWarnings.Select(r => $"dotnet_diagnostic.{r}.severity = warning"));
        var silents = string.Join('\n', CSharpStyleConvention.MustNotBeWarnings.Select(r => $"dotnet_diagnostic.{r}.severity = silent"));
        var good = $"root = true\n\n{section}\n{warnings}\n{silents}\n";

        Assert.Empty(CSharpStyleConvention.EffectiveSeverityProblems(good, editorConfig, projects));

        // severity 行をまるごと消す
        Assert.NotEmpty(CSharpStyleConvention.EffectiveSeverityProblems(
            $"root = true\n\n{section}\n", editorConfig, projects));

        // 後ろに広いセクションを足して打ち消す
        Assert.NotEmpty(CSharpStyleConvention.EffectiveSeverityProblems(
            good + "\n[BusinessApp/**.cs]\ndotnet_diagnostic.IDE0055.severity = none\n", editorConfig, projects));

        // 強制しないと決めた規則が警告に格上げされている
        Assert.NotEmpty(CSharpStyleConvention.EffectiveSeverityProblems(
            good + $"\n{section}\ndotnet_diagnostic.IDE0305.severity = warning\n", editorConfig, projects));

        // 対象外のはずのプロジェクトにまで当たっている
        Assert.NotEmpty(CSharpStyleConvention.EffectiveSeverityProblems(
            good + "\n[BusinessApp/BusinessApp.Server/**.cs]\ndotnet_diagnostic.IDE0055.severity = warning\n",
            editorConfig, projects));

        Assert.NotEmpty(CSharpStyleConvention.EffectiveSeverityProblems(null, editorConfig, projects));
    }

    /// <summary>ソリューションから漏れたプロジェクトの検査は、入れ子でも鳴る。</summary>
    [Fact]
    public void ソリューションに載った知らないプロジェクトで鳴る()
    {
        var known = CSharpStyleConvention.EnforcedProjects
            .Select(name => (Name: name, Path: $"BusinessApp/{name}/{name}.csproj"))
            .ToList();

        Assert.Empty(CSharpStyleConvention.UnlistedProjects(known));
        Assert.NotEmpty(CSharpStyleConvention.UnlistedProjects(
            [.. known, ("Probe", "BusinessApp/Nested/Probe/Probe.csproj")]));
    }

    [Fact]
    public void ソリューションの読み取りは入れ子のフォルダも拾う()
    {
        var projects = CSharpStyleConvention.SolutionProjectsIn(
            """
            <Solution>
              <Folder Name="/A/">
                <Project Path="BusinessApp/One/One.csproj" />
              </Folder>
              <Project Path="BusinessApp/Two/Two.csproj" />
            </Solution>
            """);

        Assert.Equal(["One", "Two"], projects.Select(project => project.Name));
    }

    /// <summary>
    /// <c>.gitattributes</c> の改行の固定そのものを見る。
    /// </summary>
    /// <remarks>
    /// この行を消しても、既にある作業コピーは LF のままなので CRLF 検査は緑を返す。
    /// <b>壊れが出るのは、別のマシンが clone した後である</b>（qa/02 R8-15）。
    /// </remarks>
    [Theory]
    [InlineData("* text=auto\n*.cs text eol=lf\n", 0)]
    [InlineData("* text=auto\n", 1)]
    [InlineData("* text=auto\n*.cs text\n", 1)]
    [InlineData(null, 1)]
    public void gitattributes_が改行を固定していないと鳴る(string? gitAttributes, int expected)
        => Assert.Equal(expected, CSharpStyleConvention.GitAttributesProblems(gitAttributes).Count);

    /// <summary>CRLF の走査は、CR のあるソースで鳴り、無ければ黙る。</summary>
    [Fact]
    public void CRLF_の走査は_CR_のあるソースで鳴る()
    {
        var crlf = "namespace X;" + ((char)13) + ((char)10) + "class Y { }";

        Assert.NotEmpty(CSharpStyleConvention.CarriageReturnProblems([("X.cs", crlf)]));
        Assert.Empty(CSharpStyleConvention.CarriageReturnProblems([("X.cs", "namespace X;" + ((char)10) + "class Y { }")]));
    }

    private static IReadOnlyList<(int Line, string Message)> Find(
        string source,
        ForbiddenFormSet sets = ForbiddenFormSet.Everywhere,
        bool isClbScript = false)
        => CSharpStyleConvention.FindForbiddenForms(source, sets, isClbScript);

    private static void AssertNone(IReadOnlyList<string> problems)
        => Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
}
