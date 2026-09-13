namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Globalization;

/// <summary>
/// 不変条件のカタログ（docs/10 §1）から見て、<b>誰も触れていない番号を数える</b>（qa/05 §5・ADR-0054）。
/// </summary>
/// <remarks>
/// <b>このファイルにカタログの番号を字面で書かない。</b> 書くと走査が「参照済み」と数える。
/// 検体は <see cref="Specimen"/> が組み立てる（<c>#</c> が番号の接頭辞に化ける）。
/// <b>字面が入っていないことは <see cref="計器のソースは自分で自分を埋めていない"/> が毎回確かめる。</b>
/// </remarks>
public class InvariantTraceabilityTests
{
    /// <summary>
    /// <b>いま参照されていない不変条件の数。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>ラチェットである</b>（ADR-0012 §8 の Stryker・ADR-0053 の制約ノックアウトと同じ作法）。
    /// <b>「この番号は数えなくてよい」という表は持たない</b>——許容リストは必ず腐る（ADR-0012 §4）。
    /// <b>増えても減っても赤くなる</b>——<c>&lt;=</c> だけだと、テストを足して減らしても
    /// <b>誰も値を下げないまま緩い上限が残る</b>。</para>
    /// <para><b>残っているのは帳票と税集計の観点で、フェーズ 3 以降である</b>（docs/04 §3）。
    /// <b>触れるテストが無いのは穴ではなく、まだそこに来ていないということ</b>で、その回に 0 へ落ちる。</para>
    /// <para><b>ただし「参照済み」の側も安心材料ではない。</b> 同じく未実装の不変条件が、
    /// <b>別の機能のテストの説明文に名前が出ているというだけで参照済みに数えられている</b>
    /// （2026-09-14 の実測。<c>ReferencesByFile</c> が参照元を返すのはそれを人が見るためである）。
    /// <b>この計器が測れるのは「名前が書かれたか」までで、「検査されたか」ではない。</b></para>
    /// </remarks>
    private const int UnreferencedCount = 3;

    /// <summary>走査が届いているべきテストプロジェクトの数の下限。</summary>
    /// <remarks>
    /// <b>走査が狭くなると、未参照が増えて赤くなる</b>——そちらは自力で気づける。
    /// <b>置いてあるのは逆のため</b>で、除外の条件を書き換えて<b>1 プロジェクトしか見なくなった</b>ときに、
    /// 上限を上げて黙らせる道を塞ぐ。
    /// </remarks>
    private const int MinimumScannedProjects = 6;

    /// <summary>走査するソースの本数の下限。</summary>
    private const int MinimumScannedSources = 100;

    private static string Root => CSharpStyleConvention.FindRepositoryRoot();

    /// <summary>番号を組み立てる。<b>字面で書かないため</b>にある。</summary>
    private static string Id(int number) => "I-" + number.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>検体の <c>#</c> を番号の接頭辞に化けさせる。<b>字面で書かないため</b>にある。</summary>
    private static string Specimen(string template) => template.Replace("#", "I-", StringComparison.Ordinal);

    [Fact]
    public void カタログに載っているのにテストが触れていない不変条件はちょうど記録した数だけある()
    {
        var catalog = InvariantTraceabilityConvention.CatalogIn(InvariantTraceabilityConvention.CatalogText(Root));
        var references = InvariantTraceabilityConvention.ReferencesByFile(Root);
        var unreferenced = InvariantTraceabilityConvention.Unreferenced(catalog, references.Keys);

        Assert.True(
            unreferenced.Count == UnreferencedCount,
            $"テストがどこからも触れていない不変条件が {unreferenced.Count} 件で、"
            + $"記録した {UnreferencedCount} 件と違う（{string.Join(" / ", unreferenced)}）。"
            + "増えたならテストを足すこと。減ったならこの値を下げること——どちらも黙って通さない。");
    }

    /// <summary>
    /// <b>未参照の数え方そのものを検体で固定する。</b>
    /// </summary>
    /// <remarks>
    /// <b>実物のリポジトリだけで確かめると、判定の中身を空にしても緑のままになる</b>
    /// （<c>CSharpStyleConvention</c> が明記して避けている形。qa/02 の R8-11）。
    /// <b>ちょうど 1 件だけ未参照になる検体</b>を置く——0 件でも全件でもない形でなければ、
    /// 「何も返さない」実装と「全部返す」実装の両方を落とせない。
    /// </remarks>
    [Fact]
    public void 未参照の数え方は検体で固定されている()
    {
        var catalog = InvariantTraceabilityConvention.CatalogIn(Specimen("""
            | 番号 | 内容 |
            |---|---|
            | #01 | 伝票単位で貸借一致 |
            | #02 | 帳簿ぜんたいで貸借一致 |
            | #03 | 有効な会計期間に属する |

            本文に #04 と書いても、行頭の縦棒が無いのでカタログには入らない。
            """));
        var referenced = InvariantTraceabilityConvention.ReferencesIn(
            Specimen("#01 を見るテスト。#03 も見る。#09 はカタログに無い番号。"));

        Assert.Equal([Id(1), Id(2), Id(3)], catalog);
        Assert.Equal([Id(1), Id(3), Id(9)], referenced);
        Assert.Equal([Id(2)], InvariantTraceabilityConvention.Unreferenced(catalog, referenced));
    }

    /// <summary>
    /// <b>0 件は緑ではない。</b> カタログを読めなくなったら、未参照は 0 件になって永久に緑を返す。
    /// </summary>
    /// <remarks>
    /// <b>読めなかった行も数える。</b> 表を整形し直して 1 行だけ拾えなくなると、
    /// <b>その番号は母数からも未参照からも消える</b>——件数の下限だけでは気づけない。
    /// </remarks>
    [Fact]
    public void カタログを一行残らず読めている()
    {
        var text = InvariantTraceabilityConvention.CatalogText(Root);
        var catalog = InvariantTraceabilityConvention.CatalogIn(text);

        Assert.True(
            catalog.Count >= InvariantTraceabilityConvention.MinimumCatalogEntries,
            $"カタログから {catalog.Count} 件しか読めていない"
            + $"（下限 {InvariantTraceabilityConvention.MinimumCatalogEntries}）。"
            + "表の書式が変わったのなら拾い方を直すこと。不変条件を減らしたのなら下限を下げること。");
        Assert.Empty(InvariantTraceabilityConvention.UnreadableCatalogRowsIn(text));
    }

    /// <summary>
    /// <b>カタログの行の拾い方を検体で固定する。</b>
    /// </summary>
    [Theory]
    [InlineData("| #01 | 伝票単位で貸借一致 |", new[] { 1 })]
    [InlineData("|#01|詰めて書いても拾う|", new[] { 1 })]
    [InlineData("|  #01  | 空白を増やしても拾う |", new[] { 1 })]
    [InlineData("地の文の #01 は拾わない", new int[0])]
    [InlineData("| R-#1 | 取引先側の不変条件 |", new int[0])]
    public void カタログの行の拾い方は書式の揺れに耐える(string row, int[] expected)
    {
        var catalog = InvariantTraceabilityConvention.CatalogIn(Specimen(row));

        Assert.Equal(expected.Select(Id), catalog);
    }

    /// <summary>
    /// <b>読めなかった行の見つけ方を検体で固定する。</b>
    /// </summary>
    [Fact]
    public void 番号の行に見えるのに読めなかった行を見つけられる()
    {
        var unreadable = InvariantTraceabilityConvention.UnreadableCatalogRowsIn(
            Specimen("""
                | #01 | 読める |
                | #1 | ゼロ詰めが落ちていて読めない |
                | 番号 | 内容 |
                """));

        Assert.Equal(Specimen("| #1 |"), Assert.Single(unreadable).TrimEnd());
    }

    /// <summary>
    /// <b>走査が届く範囲が、狭すぎも広すぎもしない。</b>
    /// </summary>
    /// <remarks>
    /// <b>狭くなると未参照が増えて赤くなるので自力で気づける。</b>
    /// <b>広くなると静かに緑になる</b>——DDL の注記やテストの土台まで数えると、
    /// <b>守りを書いただけ・道具を書いただけで観点が埋まる</b>。そちらを止める。
    /// </remarks>
    [Fact]
    public void 走査する範囲はテストプロジェクトに限られている()
    {
        var root = Root;
        var scanned = InvariantTraceabilityConvention.TestSources(root)
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
    /// 入れた回に実際に踏んだ——3 つの番号を説明に書いたら、未参照が 3 件から 1 件に減った
    /// （2026-09-14。qa/02 のラウンド 90）。<b>説明を書くほど検査が甘くなる</b>形である。
    /// </remarks>
    [Fact]
    public void 計器は自分のソースを走査しない()
    {
        var root = Root;
        var own = InvariantTraceabilityConvention.OwnSources(root).ToList();
        var scanned = InvariantTraceabilityConvention.TestSources(root).ToList();

        Assert.Equal(2, own.Count);
        Assert.All(own, file => Assert.DoesNotContain(file, scanned, StringComparer.Ordinal));
    }

    /// <summary>
    /// <b>計器のソースに、カタログの番号が字面で入っていない。</b>
    /// </summary>
    /// <remarks>
    /// <b>上の除外はファイル名で決まっており、改名や分割で崩れる</b>——
    /// 崩れた日に<b>この 2 ファイルが自分で自分を埋めて、上限が黙って甘くなる</b>（qa/02 R8-12 と同じ型）。
    /// <b>名前ではなく中身で見る</b>ので、どこへ動かしても守りが残る。
    /// </remarks>
    [Fact]
    public void 計器のソースは自分で自分を埋めていない()
    {
        var own = InvariantTraceabilityConvention.OwnSources(Root).ToList();

        Assert.NotEmpty(own);
        Assert.All(own, file => Assert.Empty(
            InvariantTraceabilityConvention.ReferencesIn(File.ReadAllText(file))));
    }

    /// <summary>
    /// <b>取引先側の番号（docs/14 §5 の <c>R-I1</c>〜<c>R-I9</c>）を拾わない。</b>
    /// </summary>
    /// <remarks>
    /// <b>桁も縛る。</b> 緩い正規表現は <c>R-I5</c> の後半を拾ってしまい、
    /// <b>存在しない番号を「参照済み」に数える</b>——上限が黙って甘くなる。
    /// <b>拾えない形も検体に置く</b>（下 3 つ）。拾えないこと自体は安全側だが、
    /// <b>知らずに書くと「テストがあるのに未参照のまま」になる</b>ので、形を固定して見えるようにしておく。
    /// </remarks>
    [Theory]
    [InlineData("#05 を守る", new[] { 5 })]
    [InlineData("// R-#5 は取引先側の不変条件", new int[0])]
    [InlineData("R-#15 と #15", new[] { 15 })]
    [InlineData("（#01・#17）", new[] { 1, 17 })]
    [InlineData("#08、句読点の直前でも拾う", new[] { 8 })]
    // **日本語が直前に付くと拾えない**（.NET の \b は日本語も語の文字として扱う）。
    [InlineData("不変条件#05", new int[0])]
    // **範囲の書き方は両端しか拾えない。** 間の番号は未参照のまま残る。
    [InlineData("#11〜#13", new[] { 11, 13 })]
    // **ゼロ詰めと桁数が違うものは拾わない。**
    [InlineData("#5 はカタログの書き方ではない", new int[0])]
    [InlineData("#011 は 3 桁なので拾わない", new int[0])]
    public void 参照の拾い方は取引先側の番号と桁で縛られている(string source, int[] expected)
    {
        var found = InvariantTraceabilityConvention.ReferencesIn(Specimen(source));

        Assert.Equal(expected.Select(Id), found);
    }
}
