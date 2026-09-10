namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Text.RegularExpressions;

/// <summary>
/// <b>会計コアのドメイン層は、会計補助の名前を持たない</b>（ADR-0049 の決定 6）。
/// </summary>
/// <remarks>
/// <para>補助で使われるものでも、ドメインは会計コアの名前で持つ。<b>ドメインに「複製」の語が現れたら、
/// 置き場を間違えている</b>——実際に <c>IsDuplicable</c>・<c>DescriptionForDuplicate</c> が
/// ドメインに増えたのを、2026-09-10 に消した（qa/03 L-40）。</para>
/// <para><b>コメントも含めて見る。</b> 識別子だけを見ると、「複製が呼ぶ」と説明するコメントから
/// 次の識別子が生まれる。語を増やすときは、まずその概念が会計コアかを疑う。</para>
/// </remarks>
public class DomainVocabularyTests
{
    /// <summary>
    /// 補助の語。<c>Duplicated</c>（行番号の重複）は「重複」の意味なので除く。
    /// </summary>
    private static readonly Regex Assistance = new(
        @"複製|Duplicat(?!ed)", RegexOptions.CultureInvariant);

    [Fact]
    public void ドメインに会計補助の語が無い()
    {
        var offenders = ProjectPaths.SourceFiles(ProjectPaths.SourceProject)
            .SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (file, index, line))
                .Where(t => Assistance.IsMatch(t.line))
                .Select(t => $"{Path.GetRelativePath(ProjectPaths.SourceProject, t.file)}:{t.index + 1}: {t.line.Trim()}"))
            .ToList();

        Assert.True(offenders.Count == 0, "ドメイン層に会計補助の語がある（ADR-0049 の決定 6）:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void 見ているファイルが空でない()
        => Assert.True(ProjectPaths.SourceFiles(ProjectPaths.SourceProject).Count() > 30, "ドメインのソースを集められていない。");
}
