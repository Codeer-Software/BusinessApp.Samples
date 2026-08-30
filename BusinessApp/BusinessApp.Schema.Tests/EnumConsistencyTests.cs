namespace BusinessApp.Schema.Tests;

using BusinessApp.ServerSupport;
using BusinessApp.TestSupport;

using System.Reflection;
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
        { "科目区分",                  "accounts.category",                   "AccountCategories", "BusinessApp.AccountingCore.Accounts.AccountCategory" },
        { "課税区分",                  "tax_categories.taxation_type",        "TaxationTypes",     null },
        { "税率区分",                  "tax_categories.rate_kind",            "RateKinds",         null },
        { "用途区分（税区分の初期値）", "tax_categories.default_tax_treatment", "TaxTreatments",     "BusinessApp.AccountingCore.ConsumptionTax.TaxTreatment" },
        { "用途区分（仕訳明細）",       "journal_lines.tax_treatment",         "TaxTreatments",     "BusinessApp.AccountingCore.ConsumptionTax.TaxTreatment" },
        { "締めの状態（会計年度）",     "fiscal_years.status",                 "PeriodStatuses",    "BusinessApp.AccountingCore.Periods.PeriodStatus" },
        { "締めの状態（会計期間）",     "accounting_periods.status",           "PeriodStatuses",    "BusinessApp.AccountingCore.Periods.PeriodStatus" },
        { "仕訳の状態",                "journal_entries.status",              "EntryStatuses",     "BusinessApp.AccountingCore.Journals.EntryStatus" },
        { "仕訳の種別",                "journal_entries.entry_type",          "EntryTypes",        "BusinessApp.AccountingCore.Journals.EntryType" },
        { "借方貸方",                  "journal_lines.debit_credit",          "DebitCredits",      "BusinessApp.AccountingCore.Shared.DebitCredit" },
        { "取引先の種別",              "partners.entity_type",                "PartnerEntityTypes", "BusinessApp.Partners.PartnerEntityType" },
        // C# は null: 登録の判定ロジック（tax_point で引く）はフェーズ 3、取込はフェーズ 6 で作る。
        { "登録の終わりの理由",        "partner_invoice_registrations.end_reason", "RegistrationEndReasons", null },
        { "登録情報の出所",            "partner_invoice_registrations.source",     "RegistrationSources",    null },
        // C# は null: 権限を判定するのは CLB（app.clprj とフレームの条件）であって C# ではない。
        // 会計コアが役割を読む場面は無い——読むと「取引先を触るのに会計権限を確かめる」形になり、
        // 依存方向が逆になる（ADR-0026 §1 の追記③）。
        { "会計の役割",                "app_users.accounting_role",           "AccountingRoles",   null },
        { "取引先の役割",              "app_users.partner_role",              "PartnerRoles",      null },
    };

    /// <summary>
    /// 表示名を持つ区分。<b>C# と CLB で文言が一致していなければならない。</b>
    /// </summary>
    /// <remarks>
    /// 利用者に見せる文言に列挙子の英語名を混ぜないため、C# 側にも日本語名を持たせている
    /// （docs/09_画面の原則.md §2）。**写しが 2 つになるので、機械で突き合わせる。**
    /// 片方だけ直すと、画面と差し戻しの文言が食い違う。
    /// <para><b>限界: 見ているのは「2 か所が一致しているか」だけである。</b>
    /// 日本語かどうかは見ていないので、<b>CLB と C# の両方が英語名なら通る</b>。
    /// 「列挙子の英語名を出さない」を守るのは人である（docs/09 §6）。</para>
    /// </remarks>
    public static TheoryData<string, string, string> DisplayNames() => new()
    {
        //  CLB の enum          C# の型                          表示名を返す拡張メソッド
        { "EntryTypes",         "BusinessApp.AccountingCore.Journals.EntryType",            "DisplayName" },
        { "EntryStatuses",      "BusinessApp.AccountingCore.Journals.EntryStatus",          "DisplayName" },
        { "PartnerEntityTypes", "BusinessApp.Partners.PartnerEntityType",    "DisplayName" },
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
            // **`CHECK (col IS NULL OR col IN (...))` の形も拾う。** SQLite の CHECK は NULL を
            // 通すので `IS NULL OR` は冗長だが、書いてあっても区分値であることに変わりはない。
            // 拾えないと、**冗長な 1 句を足すだけで対応表の検査をすり抜けられる**
            // （2026-08-31 に app_users の役割の列で実際に起きた）。
            foreach (Match match in Regex.Matches(
                text,
                @"^\s*(\w+)\s+TEXT[^,]*?CHECK \((?:\1 IS NULL OR )?\1 IN \(",
                RegexOptions.Multiline))
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

    /// <summary>
    /// <b>デザイン enum が全部、対応表に載っているか</b>（逆向きの網）。
    /// </summary>
    /// <remarks>
    /// 表から DDL を見る検査（<see cref="区分値を持つ列はすべて対応表に載っている"/>）だけだと、
    /// <b>CLB の enum を足して DDL の CHECK を書き忘れたとき、3 者一致の検査自体が
    /// その区分を知らないまま緑になる</b>（2026-08-31 の自己レビュー）。
    /// 両向きに網を張って初めて「増えたら必ず気づく」になる。
    /// </remarks>
    [Fact]
    public void デザインenumはすべて対応表に載っている()
    {
        var declared = Mappings()
            .Select(row => (string?)row[2])
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var actual = Directory.GetFiles(DesignEnumDirectory, "*.enum.json")
            .Select(f => Path.GetFileName(f).Replace(".enum.json", string.Empty, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(actual.Where(e => !declared.Contains(e)));
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

            // 値を読む側も `IS NULL OR` の形に合わせる（拾う側と同じ理由）。
            var check = Regex.Match(
                create.Groups[1].Value,
                $@"CHECK \((?:{column} IS NULL OR )?{column} IN \(([^)]*)\)\)",
                RegexOptions.Singleline);
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

    /// <summary>
    /// 区分値を持つ C# の列挙型を探すアセンブリ。<b>部品ごとに 1 本挙げる。</b>
    /// </summary>
    /// <remarks>
    /// 取引先を独立部品にした（ADR-0025）ので、会計コアの 1 本だけを見ていると
    /// <c>BusinessApp.Partners.PartnerEntityType</c> が「型が無い」で落ちる。
    /// <b>実際に落ちた</b>（2026-08-27 の分割で、この検査が移動を捉えた）。
    /// 新しい部品が区分値を持ったら、ここに 1 行足す。
    /// </remarks>
    private static readonly Assembly[] ComponentAssemblies =
    [
        typeof(AccountingCore.Shared.Yen).Assembly,
        typeof(Partners.PartnerId).Assembly,
    ];

    /// <summary>完全修飾名（<c>BusinessApp.AccountingCore.Journals.EntryType</c>）から C# の列挙型を引く。</summary>
    private static Type CSharpEnumType(string typeName)
    {
        var type = ComponentAssemblies
            .Select(assembly => assembly.GetType(typeName))
            .FirstOrDefault(found => found is not null);
        Assert.True(type is not null, $"C# の列挙型が無い: {typeName}");
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

    /// <summary>
    /// PascalCase の列挙子名を DB の値（snake_case）に変換する。
    /// </summary>
    /// <remarks>
    /// <b>本番が書き込みに使う実装（<see cref="DbValue.ToSnakeCase(string)"/>）をそのまま呼ぶ。</b>
    /// ここに写しを持つと、<b>写しだけが規約どおりで本番が違う</b>状態を検出できない——
    /// 実際、写しは数字の前で区切るのに本番は区切らず、DDL には <c>legacy_8</c> があった。
    /// 数字を含む列挙子を C# に足した日に、本番は <c>legacy8</c> を書いて CHECK に弾かれるのに、
    /// この検査は「3 者一致」と言うところだった（2026-08-27 の自己レビュー R16-03）。
    /// </remarks>
    private static string ToSnakeCase(string name) => DbValue.ToSnakeCase(name);
}
