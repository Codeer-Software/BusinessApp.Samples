namespace BusinessApp.AccountingCore.Tests.Journals;

using System.Text.RegularExpressions;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Tests.Conventions;

/// <summary>
/// 違反コードと文書の対応。
/// </summary>
/// <remarks>
/// <b>定数を写経しない。</b> テストにもう一度同じ文字列を書くだけでは、
/// 番号を付け替えたときにこのテストしか落ちず、docs/04 は黙って古いままになる。
/// <c>docs/04 §1</c> の不変条件の表を<b>実際に読んで</b>突き合わせる。
/// </remarks>
public class JournalViolationCodesTests
{
    [Fact]
    public void 不変条件のコードは文書に実在する番号を使っている()
    {
        var declared = InvariantNumbersInDocument();

        Assert.NotEmpty(declared);
        Assert.All(InvariantCodesInUse(), code =>
            Assert.True(declared.Contains(code), $"{code} は docs/04 §1 の表に無い"));
    }

    [Fact]
    public void 不変条件でないコードはEで始まる()
    {
        var invariants = InvariantCodesInUse();

        foreach (var code in AllCodes().Where(c => !invariants.Contains(c)))
        {
            Assert.StartsWith("E-", code);
        }
    }

    [Fact]
    public void 同じコードを別の名前で二重に定義していない()
    {
        var codes = AllCodes().ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    private static IReadOnlyList<string> AllCodes()
        => typeof(JournalViolationCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    private static IReadOnlyList<string> InvariantCodesInUse()
        => AllCodes().Where(c => c.StartsWith("I-", StringComparison.Ordinal)).ToList();

    /// <summary>docs/04 §1 の表から不変条件の番号（I-01 など）を読み取る。</summary>
    private static IReadOnlySet<string> InvariantNumbersInDocument()
    {
        var path = Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(ProjectPaths.TestProject)!)!,
            "docs", "04_会計ドメイン設計.md");
        Assert.True(File.Exists(path), $"設計文書が見つからない: {path}");

        return Regex.Matches(File.ReadAllText(path), @"^\| (I-\d{2}) \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}
