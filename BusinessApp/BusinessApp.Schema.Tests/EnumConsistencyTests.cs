namespace BusinessApp.Schema.Tests;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// 区分値が 3 か所で一致していることを検査する。
/// </summary>
/// <remarks>
/// <para>同じ区分値が <b>C# の列挙型・DB の CHECK 制約・CLB のデザイン enum</b> の 3 か所にある。
/// どれか 1 つを直し忘れると、コンパイルも designcheck も通ったまま
/// 「画面で選べるのに保存できない」「保存できるのに集計から漏れる」という壊れ方をする。</para>
/// <para>DB の値は snake_case、C# の列挙子は PascalCase という規約をこのテストが定義している。
/// <c>Legacy8</c> ↔ <c>legacy_8</c> のように、数字の前でも区切る。</para>
/// </remarks>
public class EnumConsistencyTests
{
    /// <summary>
    /// 区分ごとの対応表。<b>片方しか無いものは null を明示する</b>ので、
    /// 追加するときに「もう片方はどうするか」を必ず考えることになる。
    /// </summary>
    public static TheoryData<string, string, string?, string?> Mappings() => new()
    {
        // 表示名                      DDL の 列                              CLB の enum          C# の型
        { "科目区分",                  "accounts.category",                   "AccountCategories", "Accounts.AccountCategory" },
        { "課税区分",                  "tax_categories.taxation_type",        "TaxationTypes",     null },
        { "税率区分",                  "tax_categories.rate_kind",            "RateKinds",         null },
        { "用途区分（税区分の初期値）", "tax_categories.default_tax_treatment", "TaxTreatments",     "ConsumptionTax.TaxTreatment" },
        { "用途区分（仕訳明細）",       "journal_lines.tax_treatment",         "TaxTreatments",     "ConsumptionTax.TaxTreatment" },
        { "締めの状態（会計年度）",     "fiscal_years.status",                 "PeriodStatuses",    "Periods.PeriodStatus" },
        { "締めの状態（会計期間）",     "accounting_periods.status",           "PeriodStatuses",    "Periods.PeriodStatus" },
        // 仕訳の画面はまだ無いので CLB 側の enum も無い。作るときに足す。
        { "仕訳の状態",                "journal_entries.status",              null,                "Journals.EntryStatus" },
        { "仕訳の種別",                "journal_entries.entry_type",          null,                "Journals.EntryType" },
        { "借方貸方",                  "journal_lines.debit_credit",          null,                "Shared.DebitCredit" },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void 区分値はDDLとCLBとCSharpで一致している(string label, string ddlColumn, string? clbEnum, string? csharpType)
    {
        var fromDdl = ValuesFromCheckConstraint(ddlColumn);
        Assert.True(fromDdl.Count > 0, $"{label}: {ddlColumn} の CHECK 制約から値を読み取れない");

        if (clbEnum is not null)
        {
            Assert.Equal(fromDdl, ValuesFromDesignEnum(clbEnum));
        }

        if (csharpType is not null)
        {
            Assert.Equal(fromDdl, ValuesFromCSharpEnum(csharpType));
        }
    }

    /// <summary>
    /// 検査が「1 件も見つからず素通り」で緑にならないための土台。
    /// DDL の整形を変えた拍子に正規表現が外れると、この検査は静かに形骸化する。
    /// </summary>
    [Fact]
    public void 区分値を持つ列を実際に見つけられている()
    {
        Assert.NotEmpty(CheckConstraintColumns());
        Assert.Contains("accounts.category", CheckConstraintColumns());
    }

    /// <summary>対応表に載っていない CHECK 制約が増えていないか。増やしたら表に足す。</summary>
    [Fact]
    public void 区分値を持つ列はすべて対応表に載っている()
    {
        var declared = Mappings().Select(row => (string)row[1]!).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(CheckConstraintColumns().Where(c => !declared.Contains(c)));
    }

    /// <summary>DDL の中で「値の集合を CHECK で縛っている TEXT 列」を拾う。</summary>
    private static IReadOnlyList<string> CheckConstraintColumns()
    {
        var found = new List<string>();
        foreach (var file in TestDatabase.DdlFiles())
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"^\s*(\w+)\s+TEXT[^,]*?CHECK \(\1 IN \(", RegexOptions.Multiline))
            {
                var owner = Regex.Matches(text[..match.Index], @"CREATE TABLE (\w+)").LastOrDefault()?.Groups[1].Value;
                if (owner is not null)
                {
                    found.Add($"{owner}.{match.Groups[1].Value}");
                }
            }
        }

        return found;
    }

    /// <summary>CLB のデザイン enum は複数形で名づける（qa/01 J-01）。単数形だと同名フィールドと衝突する。</summary>
    [Fact]
    public void デザインenumはすべて複数形で名づけられている()
    {
        var singular = Directory.GetFiles(DesignEnumDirectory, "*.enum.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => name![..^".enum".Length])
            .Where(name => !name.EndsWith('s'))
            .ToList();

        Assert.Empty(singular);
    }

    private static string DesignEnumDirectory { get; } =
        Path.Combine(Path.GetDirectoryName(TestDatabase.DdlDirectory)!, "Design", "Enums");

    private static IReadOnlyList<string> ValuesFromCheckConstraint(string qualifiedColumn)
    {
        var (table, column) = qualifiedColumn.Split('.') switch { var parts => (parts[0], parts[1]) };

        foreach (var file in TestDatabase.DdlFiles())
        {
            var text = File.ReadAllText(file);
            var create = Regex.Match(text, $@"CREATE TABLE {table} \((.*?)\n\);", RegexOptions.Singleline);
            if (!create.Success)
            {
                continue;
            }

            var check = Regex.Match(create.Groups[1].Value, $@"CHECK \({column} IN \(([^)]*)\)\)", RegexOptions.Singleline);
            if (check.Success)
            {
                return Regex.Matches(check.Groups[1].Value, @"'([^']+)'")
                    .Select(m => m.Groups[1].Value)
                    .OrderBy(v => v, StringComparer.Ordinal)
                    .ToList();
            }
        }

        return [];
    }

    private static IReadOnlyList<string> ValuesFromDesignEnum(string enumName)
    {
        var path = Path.Combine(DesignEnumDirectory, enumName + ".enum.json");
        Assert.True(File.Exists(path), $"デザイン enum が無い: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("Members").EnumerateArray()
            .Select(member => member.GetProperty("Value").GetString()!)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ValuesFromCSharpEnum(string relativeTypeName)
    {
        var type = typeof(AccountingCore.Shared.Yen).Assembly
            .GetType("BusinessApp.AccountingCore." + relativeTypeName);
        Assert.True(type is not null, $"C# の列挙型が無い: {relativeTypeName}");

        return Enum.GetNames(type!)
            .Select(ToSnakeCase)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>PascalCase の列挙子名を DB の値（snake_case）に変換する。数字の前でも区切る。</summary>
    private static string ToSnakeCase(string name)
        => Regex.Replace(name, @"(?<!^)((?<![A-Z])[A-Z]|(?<![0-9])[0-9])", "_$1").ToLowerInvariant();
}
