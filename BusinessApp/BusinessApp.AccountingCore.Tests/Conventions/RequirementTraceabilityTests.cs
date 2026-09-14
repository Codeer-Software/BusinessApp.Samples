namespace BusinessApp.AccountingCore.Tests.Conventions;

/// <summary>
/// 制度要件のカタログ（docs/40）から見て、<b>誰も触れていない項目を数える</b>（qa/05 §5・ADR-0057）。
/// </summary>
/// <remarks>
/// <b>このファイルにカタログの項目 ID を字面で書かない。</b> 書くと走査が「参照済み」と数える。
/// 検体は <see cref="Specimen"/> が組み立てる（<c>@</c> と <c>%</c> が頭文字に化ける）。
/// <b>字面が入っていないことは <see cref="計器のソースは自分で自分を埋めていない"/> が毎回確かめる。</b>
/// </remarks>
public class RequirementTraceabilityTests
{
    /// <summary>走査が届いているべきテストプロジェクトの数の下限。</summary>
    private const int MinimumScannedProjects = 6;

    /// <summary>走査するソースの本数の下限。</summary>
    private const int MinimumScannedSources = 100;

    private static string Root => CSharpStyleConvention.FindRepositoryRoot();

    /// <summary>検体の <c>@</c> と <c>%</c> を項目の頭文字に化けさせる。<b>字面で書かないため</b>にある。</summary>
    private static string Specimen(string template)
        => template.Replace("@", "A", StringComparison.Ordinal).Replace("%", "D", StringComparison.Ordinal);

    /// <summary>
    /// <b>「済」「一部済」なのにテストが 1 本も触れていない項目を数える。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>ラチェットである。</b> <b>増えても減っても赤くなる</b>——
    /// <c>&lt;=</c> だけだと、テストを足して減らしても<b>誰も値を下げないまま緩い上限が残る</b>。</para>
    /// <para><b>「フェーズ n」「依存しない」「対象外」は数えない。</b>
    /// <b>まだ作っていないものにテストが無いのは穴ではない</b>——
    /// この計器が言うのは「<b>できていると書いてあるのに、誰も見張っていない</b>」だけである。</para>
    /// <para><b>「参照済み」の側は安心材料ではない。</b> 測れるのは
    /// <b>テストのソースにその ID が書かれているか</b>までで、
    /// <b>書いてあるのに中身を検査していない</b>ことは見えない（qa/05 §5 が最初からそう断っている）。</para>
    /// </remarks>
    [Fact]
    public void できていると書いてあるのに誰も触れていない要件はちょうど記録した数だけある()
    {
        var text = RequirementTraceabilityConvention.CatalogText(Root);
        var required = RequirementTraceabilityConvention.DoneIn(text);
        var references = RequirementTraceabilityConvention.ReferencesByFile(Root);
        var unreferenced = RequirementTraceabilityConvention.Unreferenced(required, references.Keys);

        Assert.True(
            unreferenced.Count == RequirementTraceabilityConvention.UnreferencedCount,
            $"「済」「一部済」なのにテストがどこからも触れていない要件が {unreferenced.Count} 件で、"
            + $"記録した {RequirementTraceabilityConvention.UnreferencedCount} 件と違う"
            + $"（{string.Join(" / ", unreferenced)}）。"
            + "増えたならテストを足すこと。減ったならこの値を下げること——どちらも黙って通さない。");
    }

    /// <summary>
    /// <b>未参照の数え方そのものを検体で固定する。</b>
    /// </summary>
    /// <remarks>
    /// <b>実物のリポジトリだけで確かめると、判定の中身を空にしても緑のままになる</b>
    /// （qa/02 の R8-11）。<b>ちょうど 1 件だけ未参照になる検体</b>を置く。
    /// </remarks>
    [Fact]
    public void 未参照の数え方は検体で固定されている()
    {
        var required = RequirementTraceabilityConvention.DoneIn(Specimen("""
            | # | 要件 | 本製品で満たすもの | 状態 |
            |---|---|---|---|
            | @1 | 訂正・削除の事実 | トリガ | 済 |
            | @2 | 記載事項 | 全列が不変 | 一部済（台帳はフェーズ 5） |
            | @3 | 1 週間以内の緩和 | 使わない | 依存しない |
            | %1 | 記録項目で引ける | 3 つの欄 | フェーズ 6 |
            """));
        var referenced = RequirementTraceabilityConvention.ReferencesIn(
            Specimen("40 の @1 を見るテスト。40 の %1 は済ではない。"));

        Assert.Equal([Specimen("@1"), Specimen("@2")], required);
        Assert.Equal([Specimen("@1"), Specimen("%1")], referenced);
        Assert.Equal([Specimen("@2")], RequirementTraceabilityConvention.Unreferenced(required, referenced));
    }

    /// <summary>
    /// <b>0 件は緑ではない。</b> カタログを読めなくなったら、未参照は 0 件になって永久に緑を返す。
    /// </summary>
    [Fact]
    public void カタログを一行残らず読めている()
    {
        var text = RequirementTraceabilityConvention.CatalogText(Root);
        var catalog = RequirementTraceabilityConvention.CatalogIn(text);
        var required = RequirementTraceabilityConvention.DoneIn(text);

        Assert.True(
            catalog.Count >= RequirementTraceabilityConvention.MinimumCatalogEntries,
            $"カタログから {catalog.Count} 件しか読めていない"
            + $"（下限 {RequirementTraceabilityConvention.MinimumCatalogEntries}）。");
        Assert.True(
            required.Count >= RequirementTraceabilityConvention.MinimumDoneEntries,
            $"「済」「一部済」が {required.Count} 件しか読めていない"
            + $"（下限 {RequirementTraceabilityConvention.MinimumDoneEntries}）。"
            + "状態の書き方が変わったのなら読み方を直すこと——0 件になると全部緑になる。");
        Assert.Empty(RequirementTraceabilityConvention.UnreadableCatalogRowsIn(text));
    }

    /// <summary>
    /// <b>カタログの行の拾い方を検体で固定する。</b>
    /// </summary>
    /// <remarks>
    /// <b>プライムは 0 個以上である。</b> 1 個までで書いたら、
    /// <b>プライム 2 つの行が黙って母数から落ちた</b>（実測。2026-09-14）。
    /// </remarks>
    [Theory]
    [InlineData("| @1 | 要件 | 満たすもの | 済 |", new[] { "@1" })]
    [InlineData("|@1|詰めて書いても拾う|満たすもの|済 |", new[] { "@1" })]
    [InlineData("| @1' | プライム 1 つ | 満たすもの | 済 |", new[] { "@1'" })]
    [InlineData("| @1'' | プライム 2 つ | 満たすもの | 済 |", new[] { "@1''" })]
    [InlineData("地の文の @1 は拾わない", new string[0])]
    [InlineData("| | 継続行は ID を持たない | 満たすもの | 済 |", new string[0])]
    public void カタログの行の拾い方は書式の揺れに耐える(string row, string[] expected)
    {
        var catalog = RequirementTraceabilityConvention.CatalogIn(Specimen(row));

        Assert.Equal(expected.Select(Specimen), catalog);
    }

    /// <summary>
    /// <b>「できている」側の読み方を検体で固定する。</b>
    /// </summary>
    /// <remarks>
    /// <b>ここが狂うと母数ごと消える</b>——「済」を読めなくなった瞬間、全部が緑になる。
    /// </remarks>
    [Theory]
    [InlineData("済", true)]
    [InlineData("済（2026-08-26）", true)]
    [InlineData("一部済（画面のみ）", true)]
    [InlineData("**済**", true)]
    [InlineData("**一部済**（画面のみ）", true)]
    [InlineData("フェーズ 6", false)]
    [InlineData("依存しない", false)]
    [InlineData("対象外", false)]
    [InlineData("", false)]
    public void できている側の読み方は検体で固定されている(string status, bool expected)
        => Assert.Equal(expected, RequirementTraceabilityConvention.IsDone(status));

    /// <summary>状態は行の最後の列から読む。<b>Markdown の強調は落とす。</b></summary>
    [Theory]
    [InlineData("| @1 | 要件 | 満たすもの | 一部済（画面のみ） |", "一部済（画面のみ）")]
    [InlineData("| @1 | 要件 | 満たすもの | **一部済**（画面のみ） |", "一部済（画面のみ）")]
    public void 状態は行の最後の列で強調は落とす(string row, string expected)
        => Assert.Equal(expected, RequirementTraceabilityConvention.StatusOf(Specimen(row)));

    /// <summary>
    /// <b>参照は文書の名前を前に置かせる。</b>
    /// </summary>
    /// <remarks>
    /// <b>裸の 1 文字＋数字を拾わない。</b> 変数名にも型名にも出るので、
    /// <b>緩く書くと存在しない参照を数えて上限が黙って甘くなる</b>。
    /// </remarks>
    [Theory]
    [InlineData("40 の @1 を見る", new[] { "@1" })]
    [InlineData("docs/40 の @1 を見る", new[] { "@1" })]
    [InlineData("40 の @1' を見る", new[] { "@1'" })]
    [InlineData("40 の @1'' を見る", new[] { "@1''" })]
    [InlineData("変数 @1 は参照ではない", new string[0])]
    [InlineData("41 の @1 は別の文書", new string[0])]
    [InlineData("140 の @1 は別の文書", new string[0])]
    public void 参照は文書の名前を前に置かせる(string source, string[] expected)
        => Assert.Equal(
            expected.Select(Specimen), RequirementTraceabilityConvention.ReferencesIn(Specimen(source)));

    /// <summary>
    /// <b>読めなかった行の見つけ方を検体で固定する。</b>
    /// </summary>
    [Fact]
    public void 項目の行に見えるのに読めなかった行を見つけられる()
    {
        var unreadable = RequirementTraceabilityConvention.UnreadableCatalogRowsIn(
            Specimen("""
                | @1 | 読める | 満たすもの | 済 |
                | @ | 数字が落ちていて読めない | 満たすもの | 済 |
                | # | 要件 | 本製品で満たすもの | 状態 |
                """));

        Assert.Equal(Specimen("| @ |"), Assert.Single(unreadable).TrimEnd());
    }

    /// <summary>
    /// <b>走査が届く範囲が、狭すぎも広すぎもしない。</b>
    /// </summary>
    /// <remarks>
    /// <b>走査は不変条件の側と同じものを使う</b>（<c>TestSources</c>）ので、
    /// ここで見るのは<b>この計器の側から見ても届いているか</b>だけである。
    /// </remarks>
    [Fact]
    public void 走査する範囲はテストプロジェクトに限られている()
    {
        var root = Root;
        var scanned = RequirementTraceabilityConvention.ScannedSources(root)
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();
        var projects = scanned
            .Select(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .First(segment => segment.EndsWith(".Tests", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            scanned.Count >= MinimumScannedSources,
            $"走査できたソースが {scanned.Count} 本しかない（下限 {MinimumScannedSources}）。");
        Assert.True(
            projects.Count >= MinimumScannedProjects,
            $"走査が届いたテストプロジェクトが {projects.Count} 個しかない"
            + $"（下限 {MinimumScannedProjects}。{string.Join(" / ", projects)}）。");

        // **広くなると静かに緑になる**——道具を書いただけ・守りを書いただけで観点が埋まる。
        Assert.DoesNotContain(scanned, path => path.Contains("TestSupport", StringComparison.Ordinal));
        Assert.DoesNotContain(
            scanned,
            path => path.Split(Path.DirectorySeparatorChar).Contains("obj", StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>計器は自分のソースを走査しない。</b>
    /// </summary>
    /// <remarks>
    /// <b>数えると、上限の理由を書いた注記そのものが「参照」になる。</b>
    /// 不変条件の側で実際に踏んだ（2026-09-14。qa/02 のラウンド 90）。
    /// </remarks>
    [Fact]
    public void 計器は自分のソースを走査しない()
    {
        var root = Root;
        var own = RequirementTraceabilityConvention.OwnSources(root).ToList();
        var scanned = RequirementTraceabilityConvention.ScannedSources(root).ToList();

        // **参照を出したファイルの一覧と突き合わせない。** 計器のソースには ID の字面が無いので、
        // **除外を消してもその一覧には現れず、テストは緑のまま**になる（2026-09-14 の自己レビュー）。
        Assert.Equal(2, own.Count);
        Assert.All(own, file => Assert.DoesNotContain(file, scanned, StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>計器のソースは自分で自分を埋めていない。</b>
    /// </summary>
    /// <remarks>
    /// <b>除外は名前で決まっており、改名や分割で崩れる。</b>
    /// <b>字面を書かないことが本体の守りで、名前の除外は保険である</b>——
    /// だから<b>字面が入っていないこと自体</b>を毎回確かめる。
    /// </remarks>
    [Fact]
    public void 計器のソースは自分で自分を埋めていない()
    {
        var own = RequirementTraceabilityConvention.OwnSources(Root).ToList();

        // **0 件では通さない。** 見つけ方が壊れると、この守りが無言で緑になる。
        Assert.NotEmpty(own);
        foreach (var file in own)
        {
            Assert.Empty(RequirementTraceabilityConvention.ReferencesIn(File.ReadAllText(file)));
        }
    }
}
