namespace BusinessApp.Schema.Tests;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using BusinessApp.Partners;
using BusinessApp.ServerSupport;
using BusinessApp.TestSupport;

/// <summary>
/// 桁と長さの決まりが、C# と DDL と CLB のデザインで一致している（docs/20 §4）。
/// </summary>
/// <remarks>
/// <para><b>docs/20 §4 の表がこの行を「守れていない」と書いていた。</b>
/// 法人番号の 13 桁が 3 か所（C# の定数・DDL の <c>GLOB</c>・デザインの <c>MaxLength</c>）にあり、
/// 突き合わせるテストが無かった。<b>マスタのコードの 20 文字を足すときに、まとめて閉じた</b>（2026-09-09）。</para>
/// <para><b>3 か所に書かざるを得ないのは、DDL が SQL テキスト・デザインが JSON で、
/// どちらも C# の定数を読めないからである</b>（docs/20 §4 の「已むを得ない重複」）。
/// 許容する代わりに、必ず機械で突き合わせる。</para>
/// <para><b>片方だけ直すとどう壊れるか。</b> 画面を緩めると DB が拒んで定型文になり（qa/03 L-28）、
/// 画面を厳しくすると取込だけが通る（守りが 1 層になる）。どちらもテストは緑のままである。</para>
/// </remarks>
public class FieldLengthConsistencyTests
{
    /// <summary>コードを持つ 6 つの表と、そのモジュール（docs/12 §2-1）。</summary>
    public static TheoryData<string, string> CodedModules => new()
    {
        { "FiscalYear", "fiscal_years" },
        { "TaxCategory", "tax_categories" },
        { "Account", "accounts" },
        { "SubAccount", "sub_accounts" },
        { "Department", "departments" },
        { "Partner", "partners" },
    };

    /// <summary>
    /// コードの上限は、<b>画面が文字で見せる</b>（docs/21 §1）。
    /// </summary>
    /// <remarks>
    /// <para><b><c>MaxLength</c> は使わない。</b> HTML の <c>maxlength</c> になるので、
    /// 25 文字のコードを貼ると<b>黙って 20 文字に切って保存する</b>——
    /// 21 §0 が「いちばん悪い」と名指しした形（qa/01 A-10 の <c>MaxFractionDigits</c>）で、
    /// <b>識別子で起きると別の科目が生まれる</b>（2026-09-09 の自己レビュー）。
    /// 切らずに関門が断り、上限はプレースホルダで先に見せる。</para>
    /// <para><b>プレースホルダの数と C# の定数が離れないように、ここで突き合わせる。</b></para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(CodedModules))]
    public void コードの上限は画面の文言と_CSharp_で一致する(string module, string table)
    {
        _ = table;
        var code = FieldOf(module, "Code");

        Assert.False(
            code.TryGetProperty("MaxLength", out var max) && max.ValueKind == JsonValueKind.Number,
            $"{module}.Code に MaxLength がある。黙って切るので使わない（21 §0）");
        Assert.Contains(
            MasterCode.MaxLength.ToString(CultureInfo.InvariantCulture),
            code.GetProperty("Placeholder").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// DDL のトリガが見ている上限も、同じ数である。
    /// </summary>
    /// <remarks>
    /// <b>数を書き写さずに読む</b>——稼働しているトリガの定義から <c>LENGTH(code) &gt; N</c> を拾う。
    /// </remarks>
    [Theory]
    [MemberData(nameof(CodedModules))]
    public void コードの上限は_DDL_のトリガとも一致する(string module, string table)
    {
        _ = module;
        using var db = SchemaSeed.Create();

        var definition = TestDatabase.ScalarOf<string>(
            db,
            "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
            + $" AND name = 'trg_{table}_code_format_insert'");
        var limit = Regex.Match(definition, @"LENGTH\(NEW\.code\)\s*>\s*(?<max>\d+)");

        Assert.True(limit.Success, $"{table} のトリガに長さの上限が無い");
        Assert.Equal(
            MasterCode.MaxLength.ToString(CultureInfo.InvariantCulture),
            limit.Groups["max"].Value);
    }

    /// <summary>
    /// 法人番号の桁数も、C# と DDL とデザインで一致する。
    /// </summary>
    /// <remarks>
    /// <b>docs/20 §4 が名指しで「守れていない」と書いていた 3 か所</b>である。
    /// DDL は <c>GLOB '[0-9]…'</c> を桁の数だけ並べて書いているので、その並びの数を数える。
    /// </remarks>
    [Fact]
    public void 法人番号の桁は_CSharp_と_DDL_とデザインで一致する()
    {
        Assert.Equal(CorporateNumber.Length, MaxLengthOf("Partner", "CorporateNumber"));

        using var db = SchemaSeed.Create();
        foreach (var table in new[] { "partners", "company_profile" })
        {
            var definition = TestDatabase.ScalarOf<string>(
                db, $"SELECT sql FROM sqlite_master WHERE type = 'table' AND name = '{table}'");
            var check = Regex.Match(definition, @"corporate_number GLOB '(?<digits>(\[0-9\])+)'");

            Assert.True(check.Success, $"{table} に法人番号の桁の CHECK が無い");
            Assert.Equal(CorporateNumber.Length, check.Groups["digits"].Value.Length / "[0-9]".Length);
        }
    }

    /// <summary>デザインの 1 つの欄。</summary>
    /// <remarks>
    /// <b>JSON を読んだまま返す。</b> 呼ぶ側が見たい属性を選ぶ——
    /// 「<c>MaxLength</c> があること」と「無いこと」の両方を表明したいので、
    /// 値だけを返す口にすると片方が書けない。
    /// </remarks>
    private static JsonElement FieldOf(string module, string field)
    {
        var path = Directory
            .EnumerateFiles(TestDatabase.ModulesDirectory, $"{module}.mod.json", SearchOption.AllDirectories)
            .Single();

        // JsonDocument を using で閉じると要素が無効になるので、複製して返す。
        using var design = JsonDocument.Parse(File.ReadAllText(path));
        return design.RootElement.GetProperty("Fields").EnumerateArray()
            .Single(f => f.GetProperty("Name").GetString() == field)
            .Clone();
    }

    /// <summary>デザインの <c>MaxLength</c>。無ければ落とす（無いこと自体が食い違いである）。</summary>
    private static int MaxLengthOf(string module, string field)
    {
        var target = FieldOf(module, field);

        Assert.True(
            target.TryGetProperty("MaxLength", out var max) && max.ValueKind == JsonValueKind.Number,
            $"{module}.{field} に MaxLength が無い（画面が上限を見せていない。docs/21 §1）");

        return max.GetInt32();
    }
}
