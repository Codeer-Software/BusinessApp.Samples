namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

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
        { "仕訳の状態",                "journal_entries.status",              "EntryStatuses",     "Journals.EntryStatus" },
        { "仕訳の種別",                "journal_entries.entry_type",          "EntryTypes",        "Journals.EntryType" },
        { "借方貸方",                  "journal_lines.debit_credit",          "DebitCredits",      "Shared.DebitCredit" },
        { "取引先の種別",              "partners.entity_type",                "PartnerEntityTypes", "Partners.PartnerEntityType" },
        // C# は null: 登録の判定ロジック（tax_point で引く）はフェーズ 3、取込はフェーズ 6 で作る。
        { "登録の終わりの理由",        "partner_invoice_registrations.end_reason", "RegistrationEndReasons", null },
        { "登録情報の出所",            "partner_invoice_registrations.source",     "RegistrationSources",    null },
    };

    /// <summary>
    /// 表示名を持つ区分。<b>C# と CLB で文言が一致していなければならない。</b>
    /// </summary>
    /// <remarks>
    /// 利用者に見せる文言に列挙子の英語名を混ぜないため、C# 側にも日本語名を持たせている
    /// （CLAUDE.md §2-7）。**写しが 2 つになるので、機械で突き合わせる。**
    /// 片方だけ直すと、画面と差し戻しの文言が食い違う。
    /// </remarks>
    public static TheoryData<string, string, string> DisplayNames() => new()
    {
        //  CLB の enum          C# の型                          表示名を返す拡張メソッド
        { "EntryTypes",         "Journals.EntryType",            "DisplayName" },
        { "EntryStatuses",      "Journals.EntryStatus",          "DisplayName" },
        { "PartnerEntityTypes", "Partners.PartnerEntityType",    "DisplayName" },
    };

    [Theory]
    [MemberData(nameof(DisplayNames))]
    public void 表示名はCLBとCSharpで一致している(string clbEnum, string csharpType, string method)
    {
        var type = CSharpEnumType(csharpType);
        var extensions = type.Assembly.GetType(type.FullName + "Extensions")
            ?? throw new InvalidOperationException($"{csharpType}Extensions が無い");
        var displayName = extensions.GetMethod(method)
            ?? throw new InvalidOperationException($"{csharpType}Extensions.{method} が無い");

        var fromCSharp = Enum.GetValues(type).Cast<object>()
            .Select(v => (string)displayName.Invoke(null, [v])!)
            .ToList();

        Assert.Equal(DisplayTextsFromDesignEnum(clbEnum), fromCSharp);
    }

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

    /// <summary>
    /// デザイン enum の表示名を宣言順に読む。
    /// </summary>
    /// <remarks>
    /// <b>JsonDocument の外へ JsonElement を持ち出さない。</b> 破棄済みの読み取りになる
    /// （実際にここで踏んだ）。読むのは using の中で終わらせる。
    /// </remarks>
    private static IReadOnlyList<string> DisplayTextsFromDesignEnum(string enumName)
    {
        var path = Path.Combine(DesignEnumDirectory, enumName + ".enum.json");
        Assert.True(File.Exists(path), $"デザイン enum が無い: {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.GetProperty("Members").EnumerateArray()
            .Select(member => member.GetProperty("DisplayText").GetString()!)];
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

    /// <summary>相対名（`Journals.EntryType`）から C# の列挙型を引く。</summary>
    private static Type CSharpEnumType(string relativeTypeName)
    {
        var type = typeof(AccountingCore.Shared.Yen).Assembly
            .GetType("BusinessApp.AccountingCore." + relativeTypeName);
        Assert.True(type is not null, $"C# の列挙型が無い: {relativeTypeName}");
        return type!;
    }

    private static IReadOnlyList<string> ValuesFromCSharpEnum(string relativeTypeName)
    {
        var type = CSharpEnumType(relativeTypeName);

        return Enum.GetNames(type)
            .Select(ToSnakeCase)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>PascalCase の列挙子名を DB の値（snake_case）に変換する。数字の前でも区切る。</summary>
    private static string ToSnakeCase(string name)
        => Regex.Replace(name, @"(?<!^)((?<![A-Z])[A-Z]|(?<![0-9])[0-9])", "_$1").ToLowerInvariant();
}
