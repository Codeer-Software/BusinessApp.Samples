namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Text.RegularExpressions;

/// <summary>
/// <b>1 件にまとめている断りの一覧を、字で釘付けする</b>（docs/21 §2-6）。
/// </summary>
/// <remarks>
/// <para><b>同じ★重大が 3 回続けて再発したので置いた</b>（2026-09-16。
/// qa/02 のラウンド 118・119・122、qa/03 の L-54・L-57）。
/// 再発したのは<b>「同じコード・同じ重さの直し先が 2 つなら 2 件」を撃つ検体</b>と
/// <b>重さ（`Severity` と `HasError()`）の表明</b>で、
/// <b>どちらもカバレッジ 100% のまま抜ける</b>。</para>
/// <para><b>この検査が見るのは「一覧に載っているか」までである。</b>
/// 検体があるかは見ない——<b>まとめる断りを足した回に、ここが赤くなって気づく</b>のが仕事で、
/// <b>何を書くかは人が決める</b>。</para>
/// <para><b>字面で数える。</b> `TrySayOnce` を呼ぶ箇所の近くに出る違反コードを集める——
/// 反射では「どの断りがまとまっているか」が分からない（まとめは制御の流れで決まる）。</para>
/// </remarks>
public class GroupedRejectionTests
{
    /// <summary>
    /// <b>1 件にまとめている断り。</b> 足したら、次の 3 つを検体で固定してからここへ書く。
    /// ①<b>同じコード・同じ重さの鍵が 2 つなら 2 件</b> ②<b>`Severity` と `HasError()`</b>
    /// ③<b>場所が 1 つなら並べ書きを出さない</b>。
    /// </summary>
    private static readonly string[] Grouped =
    [
        // ①直し先の識別子で鍵にするもの
        "PartnerUnknown", "PartnerInactive",
        "AccountUnknown", "AccountInactive",
        "DepartmentUnknown", "DepartmentInactive",
        "SubAccountUnknown", "SubAccountInactive",
        // ②その断りを起こしている勘定科目で鍵にするもの（要件）
        "PartnerRequired", "SubAccountRequired", "SubAccountNotAllowed", "DepartmentMissing",
    ];

    [Fact]
    public void まとめている断りの一覧は実物と一致する()
    {
        var found = GroupedInSource();

        Assert.Equal(
            Grouped.OrderBy(code => code, StringComparer.Ordinal).ToArray(),
            found.OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void まとめている断りを集められている()
    {
        // **この網が空になる状態を許さない。** 集め方を壊すと、上の検査は
        // 「一覧も実物も空」で黙って緑になる（関門は緑で失敗する。self-review スキル §9）。
        Assert.NotEmpty(GroupedInSource());
    }

    /// <summary><c>TrySayOnce</c> を呼ぶ箇所のすぐ後ろに出る違反コードを集める。</summary>
    private static string[] GroupedInSource()
    {
        var source = File.ReadAllText(Path.Combine(
            ProjectPaths.SourceProject, "Journals", "JournalEntryValidator.cs"));
        var lines = source.Split('\n');
        var codes = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("TrySayOnce(", StringComparison.Ordinal))
            {
                continue;
            }

            // **後ろ 8 行まで見る**——`violations.Add(new Violation(` を挟んでコードが出る。
            for (var ahead = i; ahead < Math.Min(i + 8, lines.Length); ahead++)
            {
                var matched = Regex.Match(lines[ahead], @"JournalViolationCodes\.(\w+)");
                if (matched.Success)
                {
                    codes.Add(matched.Groups[1].Value);
                    break;
                }
            }
        }

        return [.. codes.Distinct(StringComparer.Ordinal)];
    }
}
