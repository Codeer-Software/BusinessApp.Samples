namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Text.RegularExpressions;

/// <summary>
/// <b>制度要件のカタログ（docs/40）と、テストからの参照を突き合わせる。</b>
/// </summary>
/// <remarks>
/// <para><b>不変条件の側と同じ計器の、制度要件の側である</b>
/// （仕組みと理由は <see cref="InvariantTraceabilityConvention"/>。判断は ADR-0054 と ADR-0057）。
/// <c>docs/qa/05_観点網羅の計器.md</c> §5 が「制度要件の側は未着手」と書いていたもの。</para>
/// <para><b>不変条件と 1 つだけ違う。</b> 不変条件は<b>全部が守られていなければならない</b>が、
/// 制度要件には<b>まだ作っていないもの</b>（フェーズ n）と<b>作らないと決めたもの</b>
/// （依存しない・対象外）がある。<b>それらに参照が無いのは穴ではない。</b>
/// だから<b>「済」と「一部済」だけを参照必須にする</b>——
/// <b>「できている」と書いてあるのにテストが 1 本もない</b>が、この計器の報せである。</para>
/// <para><b>状態はカタログの最後の列から読む。</b> 手で数え直さない——
/// <b>行が「フェーズ 6」から「済」へ変わった日に、参照必須の母数が自動で増える</b>。
/// 人が二重に管理すると、状態だけ進めて母数を増やし忘れる。</para>
/// <para><b>参照の書き方は 1 通りに決める。</b> 条番号の直書き（「電帳通達 8-13」）は
/// <b>要件の ID ではない</b>——**優良な電子帳簿の要件は 2027-01-01 に条番号が動く**
/// （qa/05 §5。出典は電帳法リサーチ §0・§14-2）ので、<b>カタログの項目 ID を正とする</b>。</para>
/// <para><b>このファイルと対のテストには、カタログの ID を字面で書かない。</b>
/// 書くと<b>走査がそれを「参照済み」と数え、説明を書くほど検査が甘くなる</b>
/// （不変条件の側で実際に踏んだ。qa/02 のラウンド 90）。</para>
/// </remarks>
public static class RequirementTraceabilityConvention
{
    /// <summary>カタログの正典。</summary>
    public const string CatalogPath = "docs/40_優良な電子帳簿の対応表.md";

    /// <summary>
    /// カタログに載っているべき項目数の下限。
    /// </summary>
    /// <remarks>
    /// <b>母数そのものにラチェットを置く。</b> 表の書式が揺れて行が落ちると、
    /// <b>未参照の件数が黙って減って上限を通る</b>——読めなくなったほうが緑に近づく。
    /// <b>要件を足したら、この値も上げる。</b>
    /// </remarks>
    public const int MinimumCatalogEntries = 20;

    /// <summary>
    /// 「済」「一部済」の項目数の下限。
    /// </summary>
    /// <remarks>
    /// <b>参照必須の母数にもラチェットを置く。</b> 状態の書き方が変わって読み取れなくなると、
    /// <b>参照必須が 0 件になって全部緑になる</b>——いちばん静かな壊れ方である。
    /// </remarks>
    public const int MinimumDoneEntries = 11;

    /// <summary>
    /// 「済」「一部済」なのにテストが 1 本も触れていない項目の数。<b>ちょうどこの数である。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>「以下」ではなく「ちょうど」で持つ。</b> 減ったら下げる——
    /// 下げ忘れると、次に増えたとき気づけない。</para>
    /// <para><b>残っている 1 件が何かは、赤くなったときの文言が名指しする。</b>
    /// ここに書くと走査が拾って参照済みに数えてしまう。</para>
    /// </remarks>
    public const int UnreferencedCount = 1;

    /// <summary>
    /// カタログの行。<b>行頭の <c>|</c> で縛る</b>——同じ文書の地の文にも記号は出てくる。
    /// </summary>
    /// <remarks>
    /// <b>プライムは 0 個以上で受ける。</b> 1 個までで書いたら、
    /// <b>プライム 2 つの行が黙って母数から落ちた</b>（実測。2026-09-14）。
    /// </remarks>
    private static readonly Regex CatalogRow = new(@"^\|\s*([A-G]\d{1,2}'*)\s*\|", RegexOptions.Multiline);

    /// <summary>カタログで、項目の行に見えるのに読めなかったものを見つけるための緩い形。</summary>
    /// <remarks>
    /// <b>頭文字を本体より広く取る。</b> 本体と同じ字の集合で書くと、
    /// <b>カタログに新しい頭文字が入った日に、本体も緩い形も同時に見落とす</b>——
    /// <b>母数は増えず、未参照も増えず、関門は緑のまま</b>である（qa/03 の L-15 の型）。
    /// <b>緩い形が緩くないなら、それは緩い形ではない。</b>
    /// </remarks>
    private static readonly Regex CatalogRowCandidate =
        new(@"^\|\s*([A-Za-z][^|]{0,6})\|", RegexOptions.Multiline);

    /// <summary>
    /// ソース中の参照。<b>文書の名前を前に置かせる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>裸の <c>A1</c> を拾わない。</b> 1 文字＋数字は変数名にも型名にも出るので、
    /// <b>緩く書くと存在しない参照を数えて上限が黙って甘くなる</b>
    /// （不変条件の側が取引先の <c>R-Inn</c> で踏みかけた形）。</para>
    /// <para><b>左にも境目を置く。</b> 置かないと <c>140 の</c> や <c>2040 の</c> が当たる——
    /// <b>別の文書を指した文が参照に数えられる。</b></para>
    /// <para><b>プライムは 0 個以上で受ける。</b> 1 個までにすると、
    /// <b>プライム 2 つの参照がプライム 1 つの項目として拾われ、別の項目が参照済みになる</b>
    /// （カタログの側で実際に踏んだ形の裏返し）。</para>
    /// </remarks>
    private static readonly Regex Reference = new(@"(?<![0-9])40 の ([A-G]\d{1,2}'*)");

    /// <summary>カタログの本文から項目 ID を拾う。<b>ディスクを見ない。</b></summary>
    public static IReadOnlyList<string> CatalogIn(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return [.. CatalogRow.Matches(markdown).Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// カタログのうち、<b>「済」か「一部済」</b>の項目 ID。<b>ディスクを見ない。</b>
    /// </summary>
    /// <remarks>
    /// <b>状態は行の最後の列である。</b> 継続行（1 列目が空）は自分の状態を持つが、
    /// <b>項目の状態は ID を持つ行のもの</b>を採る。
    /// </remarks>
    public static IReadOnlyList<string> DoneIn(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var done = new List<string>();

        foreach (var line in markdown.Split('\n'))
        {
            var row = line.TrimEnd('\r');
            var match = CatalogRow.Match(row);
            if (!match.Success)
            {
                continue;
            }

            if (IsDone(StatusOf(row)))
            {
                done.Add(match.Groups[1].Value);
            }
        }

        return [.. done.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>行の最後の列（状態）。</summary>
    /// <remarks>
    /// <b>Markdown の強調を落とす。</b> 同じ文書の凡例は状態語を <c>**済**</c> と太字で定義しており、
    /// <b>本表の行が同じ書式になった日に、母数から静かに落ちる</b>。
    /// </remarks>
    public static string StatusOf(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var cells = row.Trim().Trim('|').Split('|');

        return cells[^1].Replace("*", string.Empty, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// その状態が「できている」側か。
    /// </summary>
    /// <remarks>
    /// <b>「済」で始まるかだけを見る。</b>「一部済」も含む——
    /// <b>一部でもできているなら、できている分を守るテストがあるはず</b>である。
    /// 「依存しない」「対象外」「フェーズ n」は参照を求めない。
    /// </remarks>
    public static bool IsDone(string status)
    {
        ArgumentNullException.ThrowIfNull(status);

        // **ここでも強調を落とす。** 公開の口が 2 つあるので、
        // **片方だけ落とすと、素のセルを渡した呼び手が黙って false を受け取る。**
        var text = status.Replace("*", string.Empty, StringComparison.Ordinal).Trim();

        return text.StartsWith("済", StringComparison.Ordinal)
               || text.StartsWith("一部済", StringComparison.Ordinal);
    }

    /// <summary>カタログのうち、項目の行に見えるのに読めなかったもの。</summary>
    /// <remarks>
    /// <b>読めなかった行は、静かに母数から落ちる。</b> 落ちた項目は未参照にも出てこないので、
    /// <b>関門は緑になる</b>——だから「読めなかった行が 0 件」を別に見る。
    /// </remarks>
    public static IReadOnlyList<string> UnreadableCatalogRowsIn(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var readable = CatalogRow.Matches(markdown).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

        return [.. CatalogRowCandidate.Matches(markdown).Select(match => match.Value)
            .Where(row => !readable.Contains(row))];
    }

    /// <summary>1 本のソーステキストが触れている項目 ID。<b>ディスクを見ない。</b></summary>
    public static IReadOnlyList<string> ReferencesIn(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return [.. Reference.Matches(source).Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>参照必須の項目のうち、参照の集合に無いもの。<b>ディスクを見ない。</b></summary>
    public static IReadOnlyList<string> Unreferenced(
        IEnumerable<string> required, IEnumerable<string> referenced)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(referenced);
        var seen = referenced.ToHashSet(StringComparer.Ordinal);

        return [.. required.Where(id => !seen.Contains(id)).OrderBy(id => id, StringComparer.Ordinal)];
    }

    /// <summary>カタログの本文を読む。</summary>
    public static string CatalogText(string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);

        return File.ReadAllText(
            Path.Combine(repositoryRoot, CatalogPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>テストのソースが触れている項目 ID を、<b>どのファイルが触れたか</b>とともに返す。</summary>
    /// <remarks>
    /// <b>参照元を捨てない。</b> 集合だけだと、
    /// 「別のテストのコメント 1 行だけで埋まっている」形を人が見つけられない。
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ReferencesByFile(string repositoryRoot)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in ScannedSources(repositoryRoot))
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

    /// <summary>
    /// <b>この計器が実際に走査するファイル。</b>
    /// </summary>
    /// <remarks>
    /// <b>走査の列を関数として出しておく。</b> 出さないと、
    /// <b>「自分のソースを走査しない」ことを確かめるテストが、参照を出したファイルの一覧としか
    /// 突き合わせられない</b>——計器のソースには ID の字面が無いのだから、
    /// <b>除外を丸ごと消してもその一覧には現れず、テストは緑のまま</b>になる
    /// （2026-09-14 の自己レビューで指摘された。不変条件の側は走査の列と突き合わせている）。
    /// </remarks>
    public static IEnumerable<string> ScannedSources(string repositoryRoot)
    {
        ArgumentNullException.ThrowIfNull(repositoryRoot);

        return InvariantTraceabilityConvention.TestSources(repositoryRoot)
            .Where(file => !IsThisInstrument(file));
    }

    /// <summary>この計器自身のソースかどうか。<b>走査から外す。</b></summary>
    /// <remarks>
    /// <b>除外は名前で決まっており、改名や分割で崩れる</b>——
    /// だから<b>本体の守りは「自分のソースに ID を字面で書かない」ことのほう</b>である。
    /// </remarks>
    public static bool IsThisInstrument(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Path.GetFileName(path).StartsWith("RequirementTraceability", StringComparison.Ordinal);
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
