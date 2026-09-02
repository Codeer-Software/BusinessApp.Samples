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
        // 依存方向が逆になる（ADR-0034）。
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

    /// <summary>
    /// <b>書き方を変えただけで網から漏れない</b>（2026-08-31 の自己レビュー R28-16）。
    /// </summary>
    /// <remarks>
    /// もとの読み方は「列定義の行に書いた CHECK」しか拾えず、<b>下の 3 通りはすべて素通りした</b>。
    /// 素通りしても検査は緑になる——<b>網が縮んだことは誰にも見えない</b>ので、ここで固定する。
    /// </remarks>
    [Theory]
    // 素直な形（もとの読み方でも拾えた）。
    [InlineData("CREATE TABLE t (\n    kind TEXT NOT NULL CHECK (kind IN ('a', 'b'))\n);")]
    // **表制約**として別行に書く。SQLite では同じ意味である。
    [InlineData("CREATE TABLE t (\n    kind TEXT NOT NULL,\n    CHECK (kind IN ('a', 'b'))\n);")]
    // **空白を詰める。**
    [InlineData("CREATE TABLE t (\n    kind TEXT NOT NULL CHECK(kind IN('a','b'))\n);")]
    // **行内コメントに読点を打つ。** 列定義の途中を「カンマまで」で追う読み方はここで外れた。
    [InlineData("CREATE TABLE t (\n    kind TEXT NOT NULL, -- 区分。a, b の 2 つ\n    CHECK (kind IN ('a', 'b'))\n);")]
    // **NULL を許す形**（冗長な 1 句を足すだけで抜けられないこと）。
    [InlineData("CREATE TABLE t (\n    kind TEXT CHECK (kind IS NULL OR kind IN ('a', 'b'))\n);")]
    public void 書き方を変えても区分値の列を拾える(string ddl)
    {
        Assert.Equal(["t.kind"], CheckConstraintColumnsIn(ddl));
        Assert.Equal(["a", "b"], ValuesFromCheckConstraintIn(ddl, "t.kind"));
    }

    /// <summary>区分値ではないものを拾わない（鳴りっぱなしの関門は、赤を無視させる）。</summary>
    [Theory]
    // 真偽値は区分値ではない（対応する CLB の enum も C# の列挙型も持たない）。
    [InlineData("CREATE TABLE t (\n    is_active INTEGER NOT NULL CHECK (is_active IN (0, 1))\n);")]
    // **別の列を見ている条件文**（`IS NULL OR` の左右で列名が違う）。
    [InlineData("CREATE TABLE t (\n    kind TEXT,\n    other TEXT,\n    CHECK (other IS NULL OR kind IN ('a'))\n);")]
    // 範囲の CHECK は値の集合ではない。
    [InlineData("CREATE TABLE t (\n    n INTEGER NOT NULL CHECK (n > 0)\n);")]
    public void 区分値でない_CHECK_は拾わない(string ddl)
        => Assert.Empty(CheckConstraintColumnsIn(ddl));

    /// <summary>対応表に載っていない CHECK 制約が増えていないか。増やしたら表に足す。</summary>
    [Fact]
    public void 区分値を持つ列はすべて対応表に載っている()
    {
        var declared = Mappings().Select(row => (string)row[1]!).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(CheckConstraintColumns().Where(c => !declared.Contains(c)));
    }

    /// <summary>DDL の中で「値の集合を CHECK で縛っている TEXT 列」を拾う。</summary>
    private static IReadOnlyList<string> CheckConstraintColumns()
        => [.. TestDatabase.DdlFiles().SelectMany(file => CheckConstraintColumnsIn(File.ReadAllText(file)))];

    /// <summary>
    /// 1 本の DDL から、値の集合を <c>CHECK</c> で縛っている TEXT 列を拾う。
    /// </summary>
    /// <remarks>
    /// <para><b>もとは「列定義の行に書いた CHECK」しか拾えなかった</b>（2026-08-31 の自己レビュー
    /// R28-16）。<c>^\s*(\w+)\s+TEXT[^,]*?CHECK \(</c> という 1 本の正規表現で列名と CHECK を
    /// 同時に読んでいたので、<b>表制約として別行に書く・空白を詰める・行内コメントに読点を打つ</b>の
    /// どれでも外れた。外れても<b>「対応表に載っていない列は無い」と言えてしまう</b>——
    /// 網が縮んだことは誰にも見えない。</para>
    /// <para><b>読む順を変えた。</b> ①コメントを落とす ②<c>CREATE TABLE</c> の本体を切り出す
    /// ③本体の中の <c>CHECK (… IN (…))</c> を全部拾い、<b>CHECK 自身が名指ししている列名</b>を使う
    /// ④その列が TEXT かを本体で確かめる。列定義に書いても表制約に書いても同じ結果になる。</para>
    /// <para><b>TEXT に限るのは意図である。</b> <c>CHECK (is_active IN (0, 1))</c> のような
    /// 真偽値は区分値ではなく、対応する CLB の enum も C# の列挙型も持たない。</para>
    /// </remarks>
    internal static IReadOnlyList<string> CheckConstraintColumnsIn(string ddl)
    {
        var found = new List<string>();

        foreach (var (table, body) in TableBodies(ddl))
        {
            foreach (var column in CheckedColumnsIn(body))
            {
                if (Regex.IsMatch(body, $@"^\s*{column}\s+TEXT\b", RegexOptions.Multiline))
                {
                    found.Add($"{table}.{column}");
                }
            }
        }

        return found;
    }

    /// <summary>
    /// <c>CHECK (… IN (…))</c> が縛っている列の名前。
    /// </summary>
    /// <remarks>
    /// <b><c>CHECK (col IS NULL OR col IN (…))</c> の形も拾う。</b> SQLite の CHECK は NULL を
    /// 通すので <c>IS NULL OR</c> は冗長だが、書いてあっても区分値であることに変わりはない。
    /// 拾えないと、<b>冗長な 1 句を足すだけで対応表の検査をすり抜けられる</b>
    /// （2026-08-31 に <c>app_users</c> の役割の列で実際に起きた）。
    /// <b>ただし 2 つの列名が違うときは拾わない</b>——別の列を見ている条件文である。
    /// </remarks>
    private static IEnumerable<string> CheckedColumnsIn(string body)
        => Regex.Matches(body, @"CHECK\s*\(\s*(?:(\w+)\s+IS\s+NULL\s+OR\s+)?(\w+)\s+IN\s*\(")
            .Where(match => match.Groups[1].Value.Length == 0
                            || string.Equals(match.Groups[1].Value, match.Groups[2].Value, StringComparison.Ordinal))
            .Select(match => match.Groups[2].Value);

    /// <summary><c>CREATE TABLE</c> ごとの (表の名前, 括弧の中身)。コメントは落としてある。</summary>
    private static IEnumerable<(string Table, string Body)> TableBodies(string ddl)
        => Regex.Matches(WithoutComments(ddl), @"CREATE TABLE (\w+)\s*\((.*?)\n\s*\);", RegexOptions.Singleline)
            .Select(match => (match.Groups[1].Value, match.Groups[2].Value));

    /// <summary>
    /// 行コメント（<c>--</c> から行末）を落とす。
    /// </summary>
    /// <remarks>
    /// <b>コメントの中の語を DDL として読まない。</b> 読点や括弧を含む説明文が 1 行あるだけで、
    /// 「列定義の途中」を追う読み方は外れる（R28-16）。
    /// <b>文字列リテラルの中の <c>--</c> は落とさない</b>——区分値に <c>--</c> は現れないが、
    /// 落とすと値そのものが消えるので、引用符の内側は素通しする。
    /// </remarks>
    private static string WithoutComments(string ddl)
        => Regex.Replace(ddl, @"'[^'\n]*'|--[^\n]*", match => match.Value.StartsWith("--", StringComparison.Ordinal)
            ? string.Empty
            : match.Value);

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
        => TestDatabase.DdlFiles()
            .Select(file => ValuesFromCheckConstraintIn(File.ReadAllText(file), qualifiedColumn))
            .FirstOrDefault(values => values.Count > 0) ?? [];

    /// <summary>
    /// 1 本の DDL から、その列を縛っている <c>CHECK</c> の値を読む。
    /// </summary>
    /// <remarks>
    /// <b>拾う側（<see cref="CheckConstraintColumnsIn"/>）と同じ読み方にしてある。</b>
    /// 片方だけが表制約や空白詰めを読めると、<b>「列は見つかるのに値が空」</b>で落ちる——
    /// 落ちるのは良いが、原因が「網の縮み」だと分からない。
    /// </remarks>
    internal static IReadOnlyList<string> ValuesFromCheckConstraintIn(string ddl, string qualifiedColumn)
    {
        var (table, column) = qualifiedColumn.Split('.') switch { var parts => (parts[0], parts[1]) };

        foreach (var (name, body) in TableBodies(ddl))
        {
            if (!string.Equals(name, table, StringComparison.Ordinal))
            {
                continue;
            }

            var check = Regex.Match(
                body,
                $@"CHECK\s*\(\s*(?:{column}\s+IS\s+NULL\s+OR\s+)?{column}\s+IN\s*\(([^)]*)\)",
                RegexOptions.Singleline);
            if (check.Success)
            {
                return [.. Regex.Matches(check.Groups[1].Value, @"'([^']+)'")
                    .Select(m => m.Groups[1].Value)
                    .OrderBy(v => v, StringComparer.Ordinal)];
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
