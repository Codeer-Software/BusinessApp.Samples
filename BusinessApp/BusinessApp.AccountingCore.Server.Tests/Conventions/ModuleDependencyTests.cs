namespace BusinessApp.AccountingCore.Server.Tests.Conventions;

using System.Text.RegularExpressions;

using BusinessApp.TestSupport;

/// <summary>
/// サーバ層のどこが<b>取引先部品を名指ししてよいか</b>（ADR-0025 §4・ADR-0029 §2）。
/// </summary>
/// <remarks>
/// <para><b>純粋層にしか規則が無かった。</b> <c>AccountingCore.Tests</c> の
/// <c>ModuleDependencyTests</c> は「<c>Journals</c> だけが取引先部品を知ってよい」を守っているが、
/// 見ているのは <c>BusinessApp.AccountingCore</c> だけである。**サーバ層は素通りしていた**——
/// <c>Shared/DbTransactionScope.cs</c> が <c>using BusinessApp.Partners;</c> を書いても
/// 全テストが緑になる（2026-08-31 の自己レビュー R26-23。同ファイルが自分で警戒していた穴が
/// <c>.Server</c> 側に開いたままだった）。</para>
/// <para><b>粒度はフォルダにする。</b> 純粋層の表と同じ粒度であり、
/// ファイル単位にすると「ファイルを 1 枚足すたびに表を直す」だけの作業が増える。
/// <b>理由を必ず書く</b>——外してよいかを、次に見る人が判断できる形で残す。</para>
/// </remarks>
public class ModuleDependencyTests
{
    private static readonly string SourceProject = Path.Combine(
        Path.GetDirectoryName(TestLayoutConvention.FindProjectDirectory())!,
        "BusinessApp.AccountingCore.Server");

    /// <summary>取引先部品を名指ししてよいフォルダと、その理由。</summary>
    /// <remarks>
    /// キーの空文字はプロジェクト直下。<b>ここに無いフォルダから名指しすると落ちる。</b>
    /// </remarks>
    private static readonly Dictionary<string, string> FoldersAllowedToUsePartners = new(StringComparer.Ordinal)
    {
        [""] = "保存の入口。取引先部品の関門を 1 つずつ数えず、部品の入口を 1 本呼ぶ（ADR-0025 §6）",
        ["Journals"] = "帳簿の記載事項①（相手方の氏名又は名称）と登録番号を、計上時に取引先から写す（ADR-0018）",
        ["Settings"] = "自社の法人番号の判定に、取引先部品の CorporateNumber を使う（qa/02 R26-32 で「出さない」と決めた）",
    };

    [Fact]
    public void 取引先部品を名指ししてよいフォルダは限られている()
    {
        var violations = PartnerReferences()
            .Where(reference => !FoldersAllowedToUsePartners.ContainsKey(reference.Folder))
            .Select(reference =>
                $"{reference.File}: {(reference.Folder.Length == 0 ? "プロジェクト直下" : reference.Folder)} は"
                + "取引先部品に依存できない（許可: "
                + string.Join(" / ", FoldersAllowedToUsePartners.Keys.Select(key => key.Length == 0 ? "直下" : key))
                + "）")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// 許可の行が、実際に使われている。
    /// </summary>
    /// <remarks>
    /// <b>許可リストは必ず腐る。</b> 参照が消えた行を残すと、次に誰かがそのフォルダから
    /// 名指ししたときに「もともと許可されていた」ことになる。<b>使われなくなったら消す</b>ことを
    /// 機械に言わせる。
    /// </remarks>
    [Fact]
    public void 許可した相手はすべて実際に取引先部品を名指ししている()
    {
        var used = PartnerReferences().Select(reference => reference.Folder).ToHashSet(StringComparer.Ordinal);

        var stale = FoldersAllowedToUsePartners.Keys
            .Where(folder => !used.Contains(folder))
            .Select(folder => $"{(folder.Length == 0 ? "直下" : folder)} は取引先部品を名指ししていない（許可の行を消す）")
            .ToList();

        Assert.True(stale.Count == 0, string.Join(Environment.NewLine, stale));
    }

    /// <summary>検査が「1 件も見つからず素通り」で緑にならないための土台（qa/03 L-15）。</summary>
    [Fact]
    public void 取引先部品への参照を実際に見つけられている()
    {
        Assert.True(Directory.Exists(SourceProject), "BusinessApp.AccountingCore.Server が見つからない");
        Assert.NotEmpty(PartnerReferences());
    }

    /// <summary>取引先部品（<c>BusinessApp.Partners</c>）を名指ししているファイル。</summary>
    /// <remarks>
    /// <c>BusinessApp.Partners.Server</c> も同じ部品なので <c>\b</c> で境界を切って両方を拾う。
    /// <c>BusinessApp.PartnersFoo</c> のような別物には当たらない。
    /// </remarks>
    private static List<(string Folder, string File)> PartnerReferences()
    {
        var pattern = new Regex(@"\bBusinessApp\.Partners\b");
        var found = new List<(string Folder, string File)>();

        foreach (var path in ProjectFiles())
        {
            var relative = Path.GetRelativePath(SourceProject, path);
            if (pattern.IsMatch(File.ReadAllText(path)))
            {
                found.Add((Path.GetDirectoryName(relative) ?? string.Empty, relative));
            }
        }

        return found;
    }

    private static IEnumerable<string> ProjectFiles()
        => Directory.EnumerateFiles(SourceProject, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(SourceProject, path)
                .Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"));
}
