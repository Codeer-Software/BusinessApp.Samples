namespace BusinessApp.AccountingCore.Tests.Conventions;

using System.Text.RegularExpressions;

/// <summary>
/// <b>1 件にまとめている断りの一覧を、字で釘付けする</b>（docs/21 §2-6）。
/// </summary>
/// <remarks>
/// <para><b>同じ★重大が 3 回続けて再発したので置いた</b>（2026-09-16。
/// qa/02 のラウンド 118・119・122、qa/03 の L-54・L-57）。
/// 再発したのは<b>「同じコード・同じ重さの鍵が 2 つなら 2 件」を撃つ検体</b>と
/// <b>重さ（<c>Severity</c> と <c>HasError()</c>）の表明</b>で、
/// <b>どちらもカバレッジ 100% のまま抜ける</b>。</para>
/// <para><b>この検査が見るのは 2 つまでである。</b>
/// ①<b>一覧が実物と一致しているか</b> ②<b>1 つのコードが 1 か所からしか出ていないか</b>。
/// <b>検体があるかは見ない</b>——<b>まとめる断りを足した回に、ここが赤くなって気づく</b>のが仕事で、
/// <b>何を書くかは人が決める</b>。</para>
/// <para><b>字面で数える。</b> 反射では「どの断りがまとまっているか」が分からない
/// （まとめは制御の流れで決まる）。</para>
/// </remarks>
public class GroupedRejectionTests
{
    /// <summary>
    /// <b>1 件にまとめている断り。</b> 足したら、次の 3 つを検体で固定してからここへ書く。
    /// ①<b>同じコード・同じ重さの鍵が 2 つなら 2 件</b> ②<b><c>Severity</c> と <c>HasError()</c></b>
    /// ③<b>場所が 1 つなら並べ書きを出さない</b>。
    /// </summary>
    /// <remarks>
    /// <b>違反コードそのものを鍵にするもの（下の 2 つ）では、①は原理的に撃てない</b>——
    /// 鍵がコードなので、<b>鍵が 2 つならコードも 2 つ</b>になる。
    /// 代わりに<b>「別のコードなら別に言う」</b>を撃つ。
    /// </remarks>
    private static readonly string[] Grouped =
    [
        // ①直し先の識別子で鍵にするもの
        "PartnerUnknown", "PartnerInactive",
        "AccountUnknown", "AccountInactive",
        "DepartmentUnknown", "DepartmentInactive",
        "SubAccountUnknown", "SubAccountInactive",
        // ②その断りを起こしている勘定科目で鍵にするもの（要件）
        "PartnerRequired", "SubAccountRequired", "SubAccountNotAllowed", "DepartmentMissing",
        // ③違反コードそのもので鍵にするもの（行ごとに値を入れる欄）
        "AmountNotPositive", "TaxCategoryMissing",
    ];

    [Fact]
    public void まとめている断りの一覧は実物と一致する()
    {
        var found = EmittedCodes().Distinct(StringComparer.Ordinal);

        Assert.Equal(
            Grouped.OrderBy(code => code, StringComparer.Ordinal).ToArray(),
            found.OrderBy(code => code, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void まとめている断りは_1_つのコードにつき_1_か所からしか出ない()
    {
        // **まとめは「同じ文になるもの」を畳む。** 1 つのコードに文が 2 つあると、
        // **先に来たほうが後を黙らせ、2 つ目の文が静かに消える**
        // （`E-TAX-INHERIT` は 4 文、`E-TAX-PARENT` は 3 文あるので、まとめてはいけない）。
        var emitted = EmittedCodes();

        Assert.Equal(emitted.Distinct(StringComparer.Ordinal).Count(), emitted.Count);
    }

    [Fact]
    public void まとめている断りを集められている()
    {
        // **この網が空になる状態を許さない。** 集め方を壊すと、上の検査は
        // 「一覧も実物も空」で黙って緑になる（関門は緑で失敗する。self-review スキル §9）。
        Assert.NotEmpty(EmittedCodes());
    }

    /// <summary><c>TrySayOnce</c> で畳んでいる断りの<b>出すコード</b>を集める。</summary>
    /// <remarks>
    /// <b>鍵ではなく <c>new Violation(</c> の側から拾う</b>——違反コードを鍵にするものでは
    /// <c>TrySayOnce</c> の第 1 引数がコードそのものなので、
    /// <b>鍵を拾うと「鍵と出すコードが違う」実装を見落とす</b>。
    /// </remarks>
    private static List<string> EmittedCodes()
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

            // **`new Violation(` を先に探し**、その後ろ 3 行の中からコードを拾う。
            for (var ahead = i; ahead < Math.Min(i + 10, lines.Length); ahead++)
            {
                if (!lines[ahead].Contains("new Violation(", StringComparison.Ordinal))
                {
                    continue;
                }

                for (var code = ahead; code < Math.Min(ahead + 3, lines.Length); code++)
                {
                    var matched = Regex.Match(lines[code], @"JournalViolationCodes\.(\w+)");
                    if (matched.Success)
                    {
                        codes.Add(matched.Groups[1].Value);
                        break;
                    }
                }

                break;
            }
        }

        return codes;
    }
}
