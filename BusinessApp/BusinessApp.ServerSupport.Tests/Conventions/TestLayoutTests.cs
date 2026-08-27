namespace BusinessApp.ServerSupport.Tests.Conventions;

using BusinessApp.TestSupport;

/// <summary>
/// テストの置き場所の規約（ADR-0012 §4）。判定は <see cref="TestLayoutConvention"/> が持つ。
/// </summary>
public class TestLayoutTests
{
    private static readonly TestLayoutConvention Convention =
        new(TestLayoutConvention.FindProjectDirectory(), "BusinessApp.ServerSupport");

    [Fact]
    public void テストファイルは対象と同じ場所と名前に置かれている()
    {
        var missing = Convention.TestsWithoutSource();
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void テストの名前空間はフォルダ構造と一致している()
    {
        var mismatched = Convention.TestsWithMismatchedNamespace();
        Assert.True(mismatched.Count == 0, string.Join(Environment.NewLine, mismatched));
    }

    [Fact]
    public void テストプロジェクトとソースプロジェクトを見つけられる()
    {
        Assert.True(Directory.Exists(Convention.SourceProject), $"{Convention.SourceProjectName} が見つからない");
        Assert.NotEmpty(Convention.MirroredTestFiles());
    }
}
