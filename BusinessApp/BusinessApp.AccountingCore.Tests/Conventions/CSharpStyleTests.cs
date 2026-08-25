namespace BusinessApp.AccountingCore.Tests.Conventions;

using BusinessApp.TestSupport;

/// <summary>
/// C# の記法規約（ADR-0021）。判定は <see cref="CSharpStyleConvention"/> が持つ。
/// </summary>
/// <remarks>
/// <para><b>ここが見るのは、アナライザで表現できないものだけ</b>である。
/// <c>.editorconfig</c> で表現できるものは <c>EnforceCodeStyleInBuild</c> がビルドで止める
/// （<c>TreatWarningsAsErrors</c> と組でエラーになる）。</para>
/// <para>リポジトリ全体を見るのでこのプロジェクトの外まで検査するが、
/// このためにテストプロジェクトを 1 つ増やすほうが高くつくのでここに置く。</para>
/// </remarks>
public class CSharpStyleTests
{
    private static readonly CSharpStyleConvention Convention =
        new(CSharpStyleConvention.FindRepositoryRoot());

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
        Assert.True(
            File.Exists(Path.Combine(Convention.RepositoryRoot, ".editorconfig")),
            ".editorconfig が無い。記法の強制はこのファイルが土台である（ADR-0021 §4）");

        // 表に書いた名前が実在すること。存在しない名前を書くと、そのプロジェクトは
        // 「表に載っているのに 1 行も検査されない」状態になる。
        var missing = CSharpStyleConvention.EnforcedProjects
            .Concat(CSharpStyleConvention.TemplateDerivedProjects)
            .Where(project => !Directory.Exists(Path.Combine(Convention.ProjectsDirectory, project)))
            .ToList();
        Assert.True(missing.Count == 0, $"表にあるが実在しない: {string.Join(" / ", missing)}");

        // 実際に読めている証拠として、この検査自身のソースを見つけられること。
        Assert.Contains(
            CSharpStyleConvention.SourceFiles(
                Path.Combine(Convention.ProjectsDirectory, "BusinessApp.AccountingCore.Tests")),
            path => path.EndsWith("CSharpStyleTests.cs", StringComparison.Ordinal));

        // CLB スクリプト（*.mod.cs）も検査の対象である（ADR-0021 §2 の禁止形は場所によらない）。
        Assert.Contains(
            CSharpStyleConvention.SourceFiles(Path.Combine(Convention.RepositoryRoot, "Designer")),
            path => path.EndsWith(".mod.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// 検査を「コードだけ」に効かせる仕掛けそのものの検査。
    /// </summary>
    /// <remarks>
    /// <b>ここが壊れると、上の 3 つは静かに何も見なくなる。</b>
    /// 消しすぎればすべてのコードが素通りし、消し足りなければ
    /// 規約そのものを説明する doc コメントが違反として報告される。
    /// </remarks>
    [Theory]
    // コメント（規約そのものを説明する文が違反にならない）
    [InlineData("// x is { } y", false)]
    [InlineData("/// <c>is { }</c> は使わない", false)]
    [InlineData("/* a is { } b */ var x = 1;", false)]
    // 文字列リテラル（SQL や画面文言の中身は見ない）
    [InlineData("var sql = \"where a is { }\";", false)]
    [InlineData("var s = $\"{x} is { } \";", false)]
    [InlineData("var v = @\"is { }\";", false)]
    // 文字列の中の // でコメントが始まったことにしない
    [InlineData("var url = \"https://example.test\"; if (x is { } y)", true)]
    // コードは見る
    [InlineData("if (x is { } y)", true)]
    [InlineData("if (x is not { } y)", true)]
    public void コメントと文字列を取り除いてから検査する(string source, bool survives)
    {
        var stripped = CSharpStyleConvention.StripCommentsAndLiterals(source);
        Assert.Equal(survives, stripped.Contains("{ }", StringComparison.Ordinal));
    }

    [Fact]
    public void 取り除いても行数は変わらない()
    {
        // 行番号がずれると、報告されたファイルの何行目かが当てにならなくなる。
        string[] lines =
        [
            "// 1 行目のコメント",
            "var sql = @\"複数行の",
            "逐語的文字列\";",
            "var raw = $\"\"\"",
            "生の文字列リテラル",
            "\"\"\";",
            "/* 複数行の",
            "   ブロックコメント */",
            "var last = 1;",
        ];
        var source = string.Join('\n', lines);

        var stripped = CSharpStyleConvention.StripCommentsAndLiterals(source);

        Assert.Equal(lines.Length, stripped.Split('\n').Length);
        Assert.Contains("var last = 1;", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("逐語的文字列", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("生の文字列リテラル", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("ブロックコメント", stripped, StringComparison.Ordinal);
    }
}
