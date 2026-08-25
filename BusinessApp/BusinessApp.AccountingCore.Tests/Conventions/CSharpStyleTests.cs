namespace BusinessApp.AccountingCore.Tests.Conventions;

using BusinessApp.TestSupport;

/// <summary>
/// C# の記法規約（ADR-0021）。判定は <see cref="CSharpStyleConvention"/> が持つ。
/// </summary>
/// <remarks>
/// <para><b>ここが見るのは、アナライザで表現できないものだけ</b>である。
/// <c>.editorconfig</c> で表現できるものは <c>EnforceCodeStyleInBuild</c> がビルドで止める
/// （<c>TreatWarningsAsErrors</c> と組でエラーになる）。</para>
/// <para><b>「違反 0 件」を表明するだけのテストは、検査そのものが壊れても緑になる。</b>
/// そこで下半分に、<b>禁止形を実際に食わせて当たることを確かめる</b>テストを置いてある。
/// 通す経路は本番と同じ <see cref="CSharpStyleConvention.FindForbiddenForms"/> である
/// （テストだけが通る別の道を作らない）。</para>
/// <para>リポジトリ全体を見るのでこのプロジェクトの外まで検査するが、
/// このためにテストプロジェクトを 1 つ増やすほうが高くつくのでここに置く。</para>
/// </remarks>
public class CSharpStyleTests
{
    private static readonly CSharpStyleConvention Convention =
        new(CSharpStyleConvention.FindRepositoryRoot());

    // ---------------------------------------------------------------------
    // 現状が規約を満たしていること
    // ---------------------------------------------------------------------

    [Fact]
    public void 禁止された記法を使っていない()
    {
        var usages = Convention.ForbiddenFormUsages();
        Assert.True(usages.Count == 0, string.Join(Environment.NewLine, usages));
    }

    [Fact]
    public void 対象プロジェクトは記法の強制を宣言している()
    {
        var missing = Convention.ProjectsWithoutEnforcement();
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void すべてのプロジェクトがどちらかの表に載っている()
    {
        var unlisted = Convention.ProjectsMissingFromTable();
        Assert.True(unlisted.Count == 0, string.Join(Environment.NewLine, unlisted));
    }

    /// <summary>
    /// <c>.editorconfig</c> の強制セクションと <c>EnforcedProjects</c> の表が一致している。
    /// </summary>
    /// <remarks>
    /// <b>ここがずれると、csproj も表も正しいのにビルドが素通りする。</b>
    /// <c>EnforceCodeStyleInBuild</c> はアナライザを走らせるだけで、
    /// どの規則をエラーにするかは <c>.editorconfig</c> のセクションが決めるからである。
    /// </remarks>
    [Fact]
    public void editorconfig_の強制セクションと表が一致している()
    {
        var mismatches = Convention.EditorConfigMismatches();
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// C# の改行が LF である。
    /// </summary>
    /// <remarks>
    /// <c>.gitattributes</c> の <c>eol=lf</c> は checkout のときにしか効かず、
    /// 既に CRLF で持っている作業コピーは直らない。IDE0055 も改行コードを見ない。
    /// <b>ここで見て初めて、機械が確かめたことになる。</b>
    /// </remarks>
    [Fact]
    public void ソースの改行は_LF_である()
    {
        var crlf = Convention.FilesWithCarriageReturns();
        Assert.True(crlf.Count == 0, string.Join(Environment.NewLine, crlf));
    }

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

        // CLB スクリプト（*.mod.cs）も検査の対象である（ADR-0021 §2 の禁止形は場所によらない）。
        Assert.Contains(files, CSharpStyleConvention.IsClbScript);
    }

    // ---------------------------------------------------------------------
    // 検査そのものが機能していること（＝わざと壊して警報が鳴るか）
    // ---------------------------------------------------------------------

    /// <summary>
    /// 空のプロパティパターンを、書き方を変えても取りこぼさない。
    /// </summary>
    /// <remarks>
    /// <b>最初は正規表現で書いていて、ここに挙げた形の半分を取りこぼしていた</b>
    /// （2026-08-26 の自己レビュー）。C# は <c>is</c> と中かっこの間に空白を要求しないし、
    /// パターンは改行をまたげる。構文木で見ればどちらにも左右されない。
    /// </remarks>
    [Theory]
    [InlineData("if (x is { } y) { }")]
    [InlineData("if (x is not { } y) { }")]
    [InlineData("if (x is{}) { }")]                             // 空白なし
    [InlineData("if (x is  {   } y) { }")]                      // 空白が複数
    [InlineData("if (x is not{} ) { }")]                        // not の後ろも空白なし
    [InlineData("var v = x switch { { } y => 1, _ => 0 };")]    // switch 式の中
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
    [InlineData("var url = \"https://example.test/is { }\";")]   // 文字列の中の // でコメントが始まらない
    [InlineData("var v = $\"{s.Split('\"')[0]} is OK\";")]       // 補間の穴の中の引用符で走査がずれない
    [InlineData("if (x is string { }) { }")]                     // 型があるので別の意味
    [InlineData("if (x is { Length: 0 }) { }")]                  // 中身があるので別の意味
    [InlineData("if (x is not null) { }")]                       // 正しい記法
    [InlineData("if (x is int v) { }")]                          // 正しい記法
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
    /// null の比較は Yoda 形（<c>null == x</c>）も捕まる。
    /// </summary>
    [Theory]
    [InlineData("if (x == null) { }", true)]
    [InlineData("if (x != null) { }", true)]
    [InlineData("if (null == x) { }", true)]
    [InlineData("if (null != x) { }", true)]
    [InlineData("if (x is null) { }", false)]
    [InlineData("if (x is not null) { }", false)]
    [InlineData("var s = \"a == null\";", false)]
    [InlineData("// x == null", false)]
    public void null_の比較は演算子ではなくパターンで書く(string source, bool caught)
        => Assert.Equal(caught, Find(source, ForbiddenFormSet.EnforcedProject).Count > 0);

    [Theory]
    [InlineData("var s = Environment.NewLine;", true)]
    [InlineData("var s = System.Environment.NewLine;", true)]
    [InlineData("var s = other.NewLine;", false)]
    public void 利用者向け文言の層では改行に_Environment_を使わない(string source, bool caught)
        => Assert.Equal(caught, Find(source, ForbiddenFormSet.MessageLayer).Count > 0);

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

    // ---------------------------------------------------------------------
    // 範囲の割り当て
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(
        "BusinessApp.AccountingCore",
        ForbiddenFormSet.Everywhere | ForbiddenFormSet.EnforcedProject | ForbiddenFormSet.MessageLayer)]
    [InlineData(
        "BusinessApp.AccountingCore.Server",
        ForbiddenFormSet.Everywhere | ForbiddenFormSet.EnforcedProject | ForbiddenFormSet.MessageLayer)]
    [InlineData("BusinessApp.AccountingCore.Tests", ForbiddenFormSet.Everywhere | ForbiddenFormSet.EnforcedProject)]
    [InlineData("BusinessApp.Server", ForbiddenFormSet.Everywhere)]
    [InlineData("BusinessApp.Client.Shared", ForbiddenFormSet.Everywhere)]
    public void プロジェクトごとに効く禁止形が決まる(string project, ForbiddenFormSet expected)
    {
        var file = Path.Combine(Convention.ProjectsDirectory, project, "Sample.cs");
        Assert.Equal(expected, Convention.SetsFor(file));
    }

    [Fact]
    public void プロジェクトの外のソースにも共通の禁止形は効く()
    {
        var script = Path.Combine(Convention.RepositoryRoot, "Designer", "Design", "Modules", "X.mod.cs");

        Assert.Equal(ForbiddenFormSet.Everywhere, Convention.SetsFor(script));
        Assert.True(CSharpStyleConvention.IsClbScript(script));
    }

    private static IReadOnlyList<(int Line, string Message)> Find(
        string source,
        ForbiddenFormSet sets = ForbiddenFormSet.Everywhere,
        bool isClbScript = false)
        => CSharpStyleConvention.FindForbiddenForms(source, sets, isClbScript);
}
