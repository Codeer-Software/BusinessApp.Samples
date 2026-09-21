namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Text.RegularExpressions;

/// <summary>
/// <b>不変条件のカタログ（docs/10 §1）と、テストからの参照を突き合わせる。</b>
/// </summary>
/// <remarks>
/// <para><b>カバレッジは「コードの網羅」しか測れない。</b> 本当に知りたいのは
/// <b>観点（仕様）の網羅</b>で、それは仕様の側から数えるしかない
/// （<c>docs/qa/05_観点網羅の計器.md</c> §5）。</para>
/// <para><b>既にある突合は片方向だけだった</b>——<c>JournalViolationCodesTests</c> は
/// 「コードが使う番号が文書の表にあるか」を見るが、<b>カタログ側から見て誰も触れていない番号</b>は
/// 誰も数えていない。<b>入れた日から既存の穴を報告する</b>のがこの計器である。</para>
/// <para><b>判定は入力を受け取って結果を返す形に切り出してある。</b>
/// <see cref="CatalogIn"/>・<see cref="ReferencesIn"/>・<see cref="Unreferenced"/> は
/// <b>ディスクを見ない</b>ので、<b>検体で振る舞いを固定できる</b>。
/// ここを混ぜると<b>中身を空にしても実物のリポジトリでは緑のまま</b>になる
/// （<see cref="CSharpStyleConvention"/> が明記して避けている形。qa/02 の R8-11）。</para>
/// <para><b>「参照」は「テストされている」ではない。</b> 測れるのは
/// <b>テストのソースにその番号が書かれているか</b>までで、
/// <b>書いてあるのに中身を検査していない</b>ことは見えない（qa/05 §5 が最初からそう断っている）。
/// それでも<b>「カタログに載っているのにテストが 1 本もない」は機械が言える</b>ようになる。</para>
/// <para><b>このファイルと対のテストには、カタログの番号を字面で書かない。</b>
/// 書くと<b>走査がそれを「参照済み」と数え、説明を書くほど検査が甘くなる</b>
/// （2026-09-14 に実際に踏んだ。qa/02 のラウンド 90）。
/// 走査から自分のファイルを外してもあるが、<b>外し方は名前で決まっている</b>——
/// 改名や分割で崩れる。<b>字面を書かないことが本体の守りで、名前の除外は保険である。</b></para>
/// </remarks>
public static class InvariantTraceabilityConvention
{
    /// <summary>カタログの正典（<c>docs/10 §1</c> の 2 列表）。</summary>
    public const string CatalogPath = "docs/10_会計ドメイン設計.md";

    /// <summary>
    /// カタログに載っているべき数の下限。
    /// </summary>
    /// <remarks>
    /// <b>母数そのものにラチェットを置く。</b> 表の書式が揺れて行が落ちると、
    /// <b>未参照の件数が黙って減って上限を通る</b>——読めなくなったほうが緑に近づく。
    /// <c>CSharpStyleConvention.MinimumEnforcedProjects</c> と同じ作法である。
    /// <b>不変条件を足したら、この値も上げる。</b>
    /// </remarks>
    public const int MinimumCatalogEntries = 17;

    private const string Prefix = "I-";

    /// <summary>
    /// カタログの行。<b>行頭の <c>|</c> で縛る</b>——同じ文書の地の文にも番号は出てくる。
    /// <b>区切りの空白は 0 個以上にしてある</b>（表を整形し直しただけで行が落ちないように）。
    /// </summary>
    private static readonly Regex CatalogRow = new(@"^\|\s*I-(\d{2})\s*\|", RegexOptions.Multiline);

    /// <summary>カタログで、番号の行に見えるのに読めなかったものを見つけるための緩い形。</summary>
    private static readonly Regex CatalogRowCandidate = new(@"^\|\s*I-[^|]*\|", RegexOptions.Multiline);

    /// <summary>
    /// ソース中の参照。<b>取引先側の <c>R-Inn</c> を拾わない</b>——
    /// docs/14 §5 に <c>R-I1</c>〜<c>R-I9</c>（取引先側の不変条件。<b>ゼロ詰め無し</b>）があり、
    /// 名前空間が衝突する。<b>緩く書くと、存在しない番号を「参照済み」に数えて上限が黙って甘くなる。</b>
    /// </summary>
    private static readonly Regex Reference = new(@"(?<!R-)\bI-(\d{2})\b");

    /// <summary>カタログの本文から番号を拾う。<b>ディスクを見ない。</b></summary>
    public static IReadOnlyList<string> CatalogIn(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return [.. CatalogRow.Matches(markdown).Select(match => Prefix + match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>カタログのうち、番号の行に見えるのに読めなかったもの。</summary>
    /// <remarks>
    /// <b>読めなかった行は、静かに母数から落ちる。</b> 落ちた番号は未参照にも出てこないので、
    /// <b>検査は緑になる</b>——だから「読めなかった行が 0 件」を別に見る。
    /// </remarks>
    public static IReadOnlyList<string> UnreadableCatalogRowsIn(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var readable = CatalogRow.Matches(markdown).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

        return [.. CatalogRowCandidate.Matches(markdown).Select(match => match.Value)
            .Where(row => !readable.Contains(row))];
    }

    /// <summary>1 本のソーステキストが触れている番号。<b>ディスクを見ない。</b></summary>
    public static IReadOnlyList<string> ReferencesIn(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return [.. Reference.Matches(source).Select(match => Prefix + match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>カタログにあって、参照の集合に無い番号。<b>ディスクを見ない。</b></summary>
    public static IReadOnlyList<string> Unreferenced(
        IEnumerable<string> catalog, IEnumerable<string> referenced)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(referenced);
        var seen = referenced.ToHashSet(StringComparer.Ordinal);

        return [.. catalog.Where(id => !seen.Contains(id)).OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>カタログの本文を読む。</summary>
    public static string CatalogText(string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);

        return File.ReadAllText(
            Path.Combine(repositoryRoot, CatalogPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// テストのソースが触れている番号を、<b>どのファイルが触れたか</b>とともに返す。
    /// </summary>
    /// <remarks>
    /// <b>参照元を捨てない。</b> 番号の集合だけだと、
    /// 「まだ作っていない機能の不変条件が、別のテストのコメント 1 行だけで埋まっている」
    /// という形を人が見つけられない（2026-09-14 の自己レビュー）。
    /// <b>参照はコメント・XML コメント・文字列リテラルの 3 通りで書かれる</b>ので、
    /// <b>ソーステキストをそのまま走査する</b>（<c>lint_docs.py</c> がコード内の文書参照を
    /// 突合しているのと同じ発想）。
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReferencesByFile(string repositoryRoot)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in TestSources(repositoryRoot))
        {
            foreach (var id in ReferencesIn(File.ReadAllText(file)))
            {
                if (!found.TryGetValue(id, out var files))
                {
                    found[id] = files = [];
                }

                files.Add(Path.GetRelativePath(repositoryRoot, file));
            }
        }

        return found.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<string>)entry.Value,
            StringComparer.Ordinal);
    }

    /// <summary>テストプロジェクトのソース。</summary>
    /// <remarks>
    /// <para><b>プロジェクト 1 つ分ではなくリポジトリ全体を見る。</b> 参照は複数のテストプロジェクトに
    /// 散っており、<c>Fixtures/</c> にも出る。<b>名前を並べず、末尾が <c>.Tests</c> の階層で決める</b>——
    /// プロジェクトが増えたときに、走査の側を直し忘れて黙って狭くならないように。</para>
    /// <para><b>数えるのは「見張る側」だけ。</b> <c>Designer/ddl/*.sql</c> の注記は数えない——
    /// DDL は守りであってテストではなく、「見張られているか」の証拠にならない。
    /// <b>テストの土台（<c>BusinessApp.TestSupport</c>）も入らない</b>
    /// （末尾が <c>.Tests</c> でないので自然に外れる）——<b>道具を書いただけで観点が埋まったことにしない。</b></para>
    /// </remarks>
    public static IEnumerable<string> TestSources(string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);

        return Directory
            .EnumerateFiles(repositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !CSharpStyleConvention.IsExcluded(Path.GetRelativePath(repositoryRoot, file)))
            .Where(file => !IsThisInstrument(file))
            .Where(file => Path.GetRelativePath(repositoryRoot, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment.EndsWith(".Tests", StringComparison.Ordinal)));
    }

    /// <summary>
    /// この計器自身のソースかどうか。<b>走査から外す。</b>
    /// </summary>
    /// <remarks>
    /// <b>実際に踏んだ。</b> 上限の理由を説明する注記に番号を 3 つ書いた瞬間、
    /// <b>その 3 つが「参照済み」になって未参照が 3 件から 1 件に減った</b>
    /// （2026-09-14。qa/02 のラウンド 90）。<b>説明を書くほど検査が甘くなる。</b>
    /// <b>この除外は名前で決まっており、改名や分割で崩れる</b>——
    /// だから<b>本体の守りは「自分のソースに番号を字面で書かない」ことのほう</b>である。
    /// </remarks>
    public static bool IsThisInstrument(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Path.GetFileName(path).StartsWith("InvariantTraceability", StringComparison.Ordinal);
    }

    /// <summary>この計器自身のソース。<b>字面を書いていないことを確かめるために要る。</b></summary>
    public static IEnumerable<string> OwnSources(string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);

        return Directory
            .EnumerateFiles(repositoryRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !CSharpStyleConvention.IsExcluded(Path.GetRelativePath(repositoryRoot, file)))
            .Where(IsThisInstrument);
    }
}
