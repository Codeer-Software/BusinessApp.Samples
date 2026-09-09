namespace BusinessApp.AccountingCore.Server.Tests.Conventions;

using System.Text.RegularExpressions;

using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.TestSupport;

/// <summary>
/// 差し戻しの文は、<b>見出しが言う結果を繰り返さない</b>（docs/21 §2-6）。
/// </summary>
/// <remarks>
/// <para>利用者が読むのは <see cref="JournalPostingRejectedException"/> が組み立てた 1 本の文で、
/// 見出し（「計上できません」ほか）＋「①…②…」の形になる。各文が結果を繰り返すと
/// <b>1 つの断りに同じ語が 2 回出る</b>——実機で見た（2026-09-08）。</para>
/// <para><b>ドメイン側は <c>AccountingCore.Tests</c> の <c>ViolationMessageTests</c> が見る。</b>
/// こちらは<b>サーバ側の関門</b>（保存・削除・取消・訂正）を受け持つ——
/// あちらは <c>AccountingCore</c> しか参照できないので、
/// <b>サーバ側の文言は網の外に落ちていた</b>（2026-09-08 の自己レビューで見つけた）。</para>
/// <para><b>ソースの字面を見る。</b> サーバ側の文は経路ごとに条件が違い、
/// 全部を実際に走らせて集めるのは検体が重くなりすぎる。
/// <b>字面で見るぶん、コメントと見出しの定義そのものは除く</b>——除き方は下の <c>Literals</c>。</para>
/// </remarks>
public class ViolationHeadlineTests
{
    /// <summary>見出しの語。<b>定義そのものは <see cref="JournalPostingRejectedException"/> が持つ。</b></summary>
    private static readonly string[] Headlines =
    [
        JournalPostingRejectedException.PostingHeadline,
        JournalPostingRejectedException.SavingHeadline,
        JournalPostingRejectedException.ReversalHeadline,
        JournalPostingRejectedException.CorrectionHeadline,
        JournalPostingRejectedException.DeletionHeadline,
        JournalPostingRejectedException.DuplicationHeadline,
    ];

    /// <summary>見出しの語を字面に持ってよいファイルと、その理由。</summary>
    /// <remarks>
    /// <b>理由を書かないと載せられない</b>（下の <c>免除の行はどれも理由を持ち_実際に要る</c> が見る）。
    /// <b>束ねずに単独で出す文は、結果を言ってよい</b>——見出しが無いので、
    /// 文が「何ができなかったか」を言わないと利用者に伝わらない。
    /// </remarks>
    private static readonly Dictionary<string, string> Exemptions = new(StringComparer.Ordinal)
    {
        ["JournalPostingRejectedException.cs"] = "見出しの語そのものを定義している",
        ["SaveFailureMessage.cs"] =
            "束ねずに単独で出す文（DB が拒んだときの受け皿）。見出しを持たないので、文が結果を言う",
        ["CompanyProfileRejectedException.cs"] = "自社情報の断りの見出しを定義している",
    };

    private static readonly string SourceProject = Path.Combine(
        Path.GetDirectoryName(TestLayoutConvention.FindProjectDirectory())!,
        "BusinessApp.AccountingCore.Server");

    [Fact]
    public void サーバ側の差し戻しの文は見出しの語を繰り返さない()
    {
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceProject, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(SourceProject, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj")
                || Exemptions.ContainsKey(Path.GetFileName(file)))
            {
                continue;
            }

            foreach (var literal in Literals(File.ReadAllText(file)))
            {
                violations.AddRange(
                    Headlines
                        .Where(headline => literal.Contains(headline, StringComparison.Ordinal))
                        .Select(headline =>
                            $"{relative}: 「{headline}」は見出しが言う。文の側では繰り返さない"
                            + $"（docs/21 §2-6）: {literal}"));
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void 免除の行はどれも理由を持ち_実際に要る()
    {
        var files = Directory
            .EnumerateFiles(SourceProject, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(SourceProject, f)
                .Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .ToDictionary(f => Path.GetFileName(f)!, f => f, StringComparer.Ordinal);

        foreach (var (name, why) in Exemptions)
        {
            Assert.False(string.IsNullOrWhiteSpace(why), $"{name} の免除に理由が無い");
            Assert.True(files.ContainsKey(name), $"免除の {name} がプロジェクトに無い（行を消す）");

            // **まだ免除が要るか。** 語を持たなくなった行が残ると、あとで戻った日に鳴らない。
            var literals = Literals(File.ReadAllText(files[name]));
            Assert.True(
                literals.Any(l => Headlines.Any(h => l.Contains(h, StringComparison.Ordinal))),
                $"{name} はもう見出しの語を持たないので、免除の行は要らない（消す）");
        }
    }

    [Fact]
    public void 文を集められている()
    {
        // **この網が何も見ていない状態を許さない。** 集め方を壊すと、上のテストは黙って緑になる。
        var found = Directory
            .EnumerateFiles(SourceProject, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(SourceProject, f)
                .Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj"))
            .SelectMany(f => Literals(File.ReadAllText(f)))
            .Count(literal => literal.Contains("ください", StringComparison.Ordinal));

        Assert.True(found > 10, $"利用者に見せる文を集められていない（{found} 件）。集め方を確かめること。");
    }

    /// <summary>
    /// ソースの中の文字列リテラル。<b>コメントは除く</b>（規則そのものを語る行が引っかかるため）。
    /// </summary>
    /// <remarks>
    /// <b>行コメントを落としてから拾う。</b> 構文木まで持ち出さないのは、
    /// 見たいのが「利用者に見せる日本語」だけで、<b>取りこぼしても過剰に鳴らない</b>側だからである
    /// （逐語の文字列に <c>//</c> は入らない）。
    /// </remarks>
    private static IEnumerable<string> Literals(string source)
    {
        var withoutComments = string.Join(
            "\n",
            source.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("//", StringComparison.Ordinal);
                return comment < 0 ? line : line[..comment];
            }));

        return Regex.Matches(withoutComments, "\"((?:[^\"\\\\\\n]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value);
    }
}
