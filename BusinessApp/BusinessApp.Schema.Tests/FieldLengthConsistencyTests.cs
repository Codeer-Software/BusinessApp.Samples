namespace BusinessApp.Schema.Tests;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Settings;
using BusinessApp.Partners;
using BusinessApp.ServerSupport;
using BusinessApp.TestSupport;

/// <summary>
/// 桁と長さの決まりが、C# と DDL と CLB のデザインで一致している（docs/20 §4）。
/// </summary>
/// <remarks>
/// <para><b>docs/20 §4 の表がこの行を「守れていない」と書いていた。</b>
/// 法人番号の 13 桁が 3 か所（C# の定数・DDL の <c>GLOB</c>・デザイン——2026-09-09 は <c>MaxLength</c>、2026-09-11 からプレースホルダ）にあり、
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
            $"{module}.Code に MaxLength がある。黙って切るので使わない（21 §1）");
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
    /// <b>文字の欄の上限</b>を持つ欄（マスタ・取引先・自社情報は docs/12 §2-2、伝票は docs/10 §4-2-1）。
    /// </summary>
    /// <remarks>
    /// <b>ここは手で書くが、手で保たない。</b> <see cref="上限を持たない文字の欄は理由つきで数え上げてある"/> が
    /// <b>デザインにある文字の欄を総当たりして、この表にも除外の表にも無い欄を赤くする</b>——
    /// <b>数を直書きして数え落とした</b>のが qa/02 のラウンド 89（日付の 3 列）で、同じ形をここで繰り返さない。
    /// </remarks>
    public static TheoryData<string, string, string, string, int> TextLengthFields => new()
    {
        { "Account", "accounts", "Name", "name", MasterTextLength.MasterName },
        { "SubAccount", "sub_accounts", "Name", "name", MasterTextLength.MasterName },
        { "Department", "departments", "Name", "name", MasterTextLength.MasterName },
        { "TaxCategory", "tax_categories", "Name", "name", MasterTextLength.MasterName },
        { "FiscalYear", "fiscal_years", "Label", "label", MasterTextLength.MasterName },
        { "Partner", "partners", "Name", "name", MasterTextLength.PartnerName },
        // **カナは名前と別の上限を持つ**（旧 Q-26 の決定。2026-09-16）。
        { "Partner", "partners", "NameKana", "name_kana", MasterTextLength.PartnerNameKana },
        { "Partner", "partners", "Address", "address", MasterTextLength.Address },
        { "Account", "accounts", "NameKana", "name_kana", MasterTextLength.MasterNameKana },
        { "SubAccount", "sub_accounts", "NameKana", "name_kana", MasterTextLength.MasterNameKana },

        // **自社情報の 5 欄**（旧 Q-26 の決定。2026-09-16）。
        // **郵便番号はここに載せない**——長さではなく書式の規則で、下で別に突き合わせている。
        { "CompanyProfile", "company_profile", "Name", "name", MasterTextLength.CompanyName },
        { "CompanyProfile", "company_profile", "NameKana", "name_kana", MasterTextLength.CompanyNameKana },
        { "CompanyProfile", "company_profile", "RepresentativeName", "representative_name", MasterTextLength.RepresentativeName },
        { "CompanyProfile", "company_profile", "Address", "address", MasterTextLength.CompanyAddress },
        { "CompanyProfile", "company_profile", "PhoneNumber", "phone_number", MasterTextLength.PhoneNumber },

        // **伝票の 2 欄だけ、上限を決めた文書も定数も違う**（docs/10 §4-2-1・`JournalLineRules`）。
        // 数が同じ 200 でも、**動く理由が違うので写さない**。
        { "JournalEntry", "journal_entries", "Description", "description", JournalLineRules.TextMaxLength },
        { "JournalLine", "journal_lines", "ItemDescription", "item_description", JournalLineRules.TextMaxLength },
    };

    /// <summary>
    /// <b>上限をまだ置いていない文字の欄</b>と、その理由（docs/12 §2-2 の「この表に無い文字の欄」）。
    /// </summary>
    /// <remarks>
    /// <b>「置いていない」を明示に持つ。</b> 黙って外すと、
    /// <b>数え落としたのか意図して外したのかが後から決まらない</b>。
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> TextFieldsWithoutLimit =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CompanyProfile.PostalCode"] = "8 文字ちょうどの書式。長さの表ではなく、この表の下で突き合わせている",
            ["CompanyProfile.CorporateNumber"] = "13 桁ちょうど。別の規則で、この表の下で突き合わせている",
            ["Partner.CorporateNumber"] = "同上",
            ["PartnerInvoiceRegistration.RegistrationNo"] = "T ＋ 13 桁ちょうど。別の規則",
            ["PartnerInvoiceRegistration.PublishedName"] = "公表システムの写しで、長さは相手が決める（開発者の決定。2026-09-16。docs/12 §2-2-1）",
            ["JournalEntry.PartnerNameSnapshot"] = "計上時の写しで、利用者は打たない（ADR-0018）",
            ["JournalLine.PartnerNameSnapshot"] = "同上",
            ["JournalLine.AppliedRuleVersion"] = "同上（適用した版の写し）",
            ["JournalLine.BookOnlyDeduction"] = "同上",
            ["JournalLine.EvidenceRef"] = "フェーズ 6（証憑の参照）。まだ画面が無い",
            ["AppUser.ユーザー識別名"] = "認証部品のもの（ADR-0032）。会計コアは間借りしているだけ",
            ["AppUser.表示名"] = "同上",
        };

    /// <summary>コードの欄は、別の規則（20 文字）で上のほうが突き合わせている。</summary>
    private const string CodeFieldName = "Code";

    /// <summary>
    /// 文字の欄の上限は、<b>画面が文字で見せる</b>（docs/21 §1）。
    /// </summary>
    /// <remarks>
    /// <b><c>MaxLength</c> は使わない。</b> HTML の <c>maxlength</c> になるので、
    /// <b>長い名前を貼ると黙って切って保存する</b>——コードと同じ理由である
    /// （docs/21 §0 が「いちばん悪い」と名指しした形。qa/01 の A-10）。
    /// </remarks>
    [Theory]
    [MemberData(nameof(TextLengthFields))]
    public void 文字の欄の上限は画面の文言と_CSharp_で一致する(
        string module, string table, string field, string column, int max)
    {
        _ = table;
        _ = column;
        var design = FieldOf(module, field);

        Assert.False(
            design.TryGetProperty("MaxLength", out var maxLength) && maxLength.ValueKind == JsonValueKind.Number,
            $"{module}.{field} に MaxLength がある。黙って切るので使わない（21 §1）");

        // **単位まで込みで比べる。** 数だけを `Contains` で見ると、
        // **30 に対して「300 文字以内」が通る**（数字が部分一致するから）。
        Assert.Contains(
            $"{max.ToString(CultureInfo.InvariantCulture)} 文字以内",
            design.GetProperty("Placeholder").GetString(),
            StringComparison.Ordinal);

        // **画面でも前後の空白を落とす。** 落とさないと、貼り付けた値の末尾の空白が
        // 上限に数えられ、**画面で数えた字数と断りの「いまは N 文字」が合わない**。
        Assert.True(
            design.GetProperty("ShouldTrimAfterEdit").GetBoolean(),
            $"{module}.{field} の ShouldTrimAfterEdit が false（前後の空白が長さに数えられる）");
    }

    /// <summary>
    /// 文字の欄の上限は、<b>DDL のトリガとも一致する</b>。
    /// </summary>
    /// <remarks>
    /// <b>数を書き写さずに読む</b>——稼働しているトリガの定義から
    /// <c>LENGTH(NEW.&lt;列&gt;) &gt; N</c> を拾う（コードの上限と同じ作法）。
    /// <b>追加と更新の 2 本とも見る</b>——片方だけ緩めると、
    /// <b>作るときは断られるのに、直すときは通る</b>という形になる。
    /// </remarks>
    [Theory]
    [MemberData(nameof(TextLengthFields))]
    public void 文字の欄の上限は_DDL_のトリガとも一致する(
        string module, string table, string field, string column, int max)
    {
        _ = module;
        _ = field;
        using var db = SchemaSeed.Create();

        foreach (var kind in new[] { "insert", "update" })
        {
            var definition = TestDatabase.ScalarOf<string>(
                db,
                "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
                + $" AND name = 'trg_{table}_{column}_length_{kind}'");

            Assert.False(
                string.IsNullOrEmpty(definition),
                $"trg_{table}_{column}_length_{kind} が無い");

            // **1 つ目だけを見ない。** トリガは `WHEN` 節と本文の `WHERE` で 2 度同じ数を書くので、
            // **`Match` だと `WHEN` 側しか見えず、本文の数だけが化けても緑になる**
            // （自己レビューのラウンド 94。2 人が独立に指摘）。
            var limits = Regex.Matches(definition, $@"LENGTH\(NEW\.{column}\)\s*>\s*(?<max>\d+)");

            // **ちょうど 2 箇所である**（`WHEN` 節と本文の `WHERE`）。`>=` にすると、
            // **枝が 3 箇所に増えても緑**のままになる。
            Assert.Equal(2, limits.Count);
            Assert.All(
                limits,
                limit => Assert.Equal(max.ToString(CultureInfo.InvariantCulture), limit.Groups["max"].Value));

            // **更新は「その列を触ったときだけ」鳴らす。** `BEFORE UPDATE ON` にすると、
            // **上限を決める前から入っていた長い行が、別の欄すら直せなくなる**
            // （移行の手前で数えて直す猶予が無くなる。0035_text_length.sql の見出し）。
            if (kind == "update")
            {
                Assert.Contains(
                    $"BEFORE UPDATE OF {column} ON {table}", definition, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// 法人番号の桁数も、C# と DDL とデザインで一致する。
    /// </summary>
    /// <remarks>
    /// <para><b>docs/20 §4 が名指しで「守れていない」と書いていた 3 か所</b>である。
    /// DDL は <c>GLOB '[0-9]…'</c> を桁の数だけ並べて書いているので、その並びの数を数える。</para>
    /// <para><b>デザインはコードと同じくプレースホルダで見せ、<c>MaxLength</c> は使わない</b>——
    /// 14 桁を貼ると黙って 13 桁に切って保存し、検査用数字が偶然合えば別の法人の番号になる（qa/01 A-13。2026-09-11）。
    /// 取引先と自社情報の 2 つの欄を見る。</para>
    /// </remarks>
    [Fact]
    public void 法人番号の桁は_CSharp_と_DDL_とデザインで一致する()
    {
        foreach (var (module, field) in new[] { ("Partner", "CorporateNumber"), ("CompanyProfile", "CorporateNumber") })
        {
            var design = FieldOf(module, field);

            Assert.False(
                design.TryGetProperty("MaxLength", out var max) && max.ValueKind == JsonValueKind.Number,
                $"{module}.{field} に MaxLength がある。黙って切るので使わない（21 §1）");
            Assert.Contains(
                CorporateNumber.Length.ToString(CultureInfo.InvariantCulture),
                design.GetProperty("Placeholder").GetString(),
                StringComparison.Ordinal);
        }

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

    /// <summary>
    /// 郵便番号の書式も、C# と DDL とデザインで一致する。
    /// </summary>
    /// <remarks>
    /// <para><b>長さではなく書式の規則である</b>（旧 Q-26 の決定。2026-09-16。docs/12 §2-2）。
    /// 7 桁 ＋ 区切りで 8 文字ちょうどにしかならないので、「N 文字以内」の形が当たらない。</para>
    /// <para><b>DDL は <c>GLOB</c> を桁の数だけ並べて書く</b>（法人番号と同じ作法）ので、その並びを数える。
    /// <b>追加と更新の 2 本とも見る</b>——片方だけ緩めると、作るときは断られるのに直すときは通る。</para>
    /// <para><b>デザインはプレースホルダで見せ、<c>MaxLength</c> は使わない</b>——黙って切るから（qa/01 A-10）。</para>
    /// </remarks>
    [Fact]
    public void 郵便番号の書式は_CSharp_と_DDL_とデザインで一致する()
    {
        var design = FieldOf("CompanyProfile", "PostalCode");

        Assert.False(
            design.TryGetProperty("MaxLength", out var max) && max.ValueKind == JsonValueKind.Number,
            "CompanyProfile.PostalCode に MaxLength がある。黙って切るので使わない（21 §1）");
        Assert.Equal(PostalCode.FormatDescription, design.GetProperty("Placeholder").GetString());
        Assert.True(
            design.GetProperty("ShouldTrimAfterEdit").GetBoolean(),
            "CompanyProfile.PostalCode の ShouldTrimAfterEdit が false（前後の空白が書式に数えられる）");

        using var db = SchemaSeed.Create();
        foreach (var kind in new[] { "insert", "update" })
        {
            var definition = TestDatabase.ScalarOf<string>(
                db,
                "SELECT sql FROM sqlite_master WHERE type = 'trigger'"
                + $" AND name = 'trg_company_profile_postal_code_format_{kind}'");

            Assert.False(
                string.IsNullOrEmpty(definition),
                $"trg_company_profile_postal_code_format_{kind} が無い");

            // **1 つ目だけを見ない**（`WHEN` 節と本文の `WHERE` で 2 度書く。長さの守りと同じ）。
            var globs = Regex.Matches(definition, @"postal_code NOT GLOB '(?<pattern>[^']+)'");
            Assert.Equal(2, globs.Count);
            Assert.All(
                globs,
                glob => Assert.Equal(
                    string.Concat(Enumerable.Repeat("[0-9]", PostalCode.PrefixLength))
                    + PostalCode.Separator
                    + string.Concat(Enumerable.Repeat("[0-9]", PostalCode.SuffixLength)),
                    glob.Groups["pattern"].Value));

            // **数えられない値も、長さの守りと同じ 3 つで断る**（qa/03 の L-48）。
            // **`GLOB` は途中の U+0000 で止まる**ので、書式が合った先にいくらでも隠せる。
            // **骨格を名指しで表明する**——3 枝を消しても `GLOB` の検査だけでは気づけない。
            Assert.Contains(
                "instr(CAST(NEW.postal_code AS BLOB), x'00') > 0", definition, StringComparison.Ordinal);
            Assert.Contains("typeof(NEW.postal_code) = 'blob'", definition, StringComparison.Ordinal);
            Assert.Contains(
                "LENGTH(CAST(NEW.postal_code AS BLOB)) > 4 * LENGTH(NEW.postal_code)",
                definition,
                StringComparison.Ordinal);

            // **断りの文言も C# と同じ字にする**（桁を変えた日に DDL だけが古くならないため）。
            Assert.Contains(PostalCode.FormatDescription, definition, StringComparison.Ordinal);

            if (kind == "update")
            {
                Assert.Contains(
                    "BEFORE UPDATE OF postal_code ON company_profile", definition, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>
    /// <b>デザインにある文字の欄は、上限を持つか、理由つきで外してあるかのどちらかである。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>母数を手で保たない。</b> 日付の守りでは列の数を直書きしていて
    /// <b>3 列を数え落とし、守ったつもりの列が守られていなかった</b>（qa/02 のラウンド 89）。
    /// 同じ形をここで繰り返さないために、<b>デザインの側から総当たりする</b>。</para>
    /// <para><b>保存先を持つモジュールの欄だけを数える。</b> クエリモジュールは <c>DbTable</c> を持たず、
    /// <b>検索欄は長さを保存しないので上限の話にならない</b>（末尾の空白は 05 の Q-25 が別に持つ）。</para>
    /// <para><b>除外は「忘れた」ではなく「決めていない」の記録である。</b>
    /// 理由が書けない欄は、除外してよい欄ではない。</para>
    /// </remarks>
    [Fact]
    public void 上限を持たない文字の欄は理由つきで数え上げてある()
    {
        var limited = TextLengthFields
            .Select(row => $"{row[0]}.{row[2]}")
            .ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     TestDatabase.ModulesDirectory, "*.mod.json", SearchOption.AllDirectories))
        {
            using var design = JsonDocument.Parse(File.ReadAllText(path));
            var module = design.RootElement.GetProperty("Name").GetString();

            // **保存先の無いモジュールは数えない**（クエリモジュールは `DbTable` を持たない。
            // 検索欄は長さを保存しないので、上限の話にならない）。
            if (string.IsNullOrEmpty(design.RootElement.GetProperty("DbTable").GetString()))
            {
                continue;
            }

            foreach (var field in design.RootElement.GetProperty("Fields").EnumerateArray())
            {
                if (field.GetProperty("TypeFullName").GetString()
                    != "Codeer.LowCode.Blazor.Repository.Design.TextFieldDesign")
                {
                    continue;
                }

                if (!field.TryGetProperty("DbColumn", out var column)
                    || string.IsNullOrEmpty(column.GetString()))
                {
                    continue;
                }

                var name = field.GetProperty("Name").GetString();
                var key = $"{module}.{name}";
                if (name == CodeFieldName || limited.Contains(key) || TextFieldsWithoutLimit.ContainsKey(key))
                {
                    continue;
                }

                missing.Add(key);
            }
        }

        Assert.True(
            missing.Count == 0,
            "文字の欄が母数からも除外の表からも漏れている（docs/12 §2-2 に書いてから、どちらかへ足すこと）: "
                + string.Join(", ", missing.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>
    /// <b>母数と除外の表の両方に居る欄は無い。</b>
    /// </summary>
    /// <remarks>
    /// <b>片方へ移してもう片方から消し忘れると、どちらの表明も緑のまま</b>である——
    /// <see cref="上限を持たない文字の欄は理由つきで数え上げてある"/> が見るのは
    /// 「<b>どちらにも無い</b>」だけだからである。
    /// <b>上限を置いた欄が「決めていない」と書かれたまま残る</b>と、次に読む人が誤る。
    /// </remarks>
    [Fact]
    public void 母数と除外の表は重ならない()
    {
        var both = TextLengthFields
            .Select(row => $"{row[0]}.{row[2]}")
            .Where(TextFieldsWithoutLimit.ContainsKey)
            .ToList();

        Assert.True(
            both.Count == 0,
            "上限を置いた欄が除外の表にも残っている（除外の行を消すこと）: "
                + string.Join(", ", both.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>
    /// <b>除外の表に、もう無い欄が残っていない。</b>
    /// </summary>
    /// <remarks>
    /// <b>除外は放っておくと腐る。</b> 欄に上限を置いた日に行を消し忘れると、
    /// <b>その欄は「決めていない」と書かれたまま守られている</b>ことになり、次に読む人が誤る。
    /// </remarks>
    [Fact]
    public void 除外の表に居ない欄が残っていない()
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(
                     TestDatabase.ModulesDirectory, "*.mod.json", SearchOption.AllDirectories))
        {
            using var design = JsonDocument.Parse(File.ReadAllText(path));
            var module = design.RootElement.GetProperty("Name").GetString();
            foreach (var field in design.RootElement.GetProperty("Fields").EnumerateArray())
            {
                actual.Add($"{module}.{field.GetProperty("Name").GetString()}");
            }
        }

        var stale = TextFieldsWithoutLimit.Keys.Where(k => !actual.Contains(k)).ToList();

        Assert.True(
            stale.Count == 0,
            "除外の表に、デザインに無い欄が残っている: " + string.Join(", ", stale.OrderBy(x => x, StringComparer.Ordinal)));
    }

    /// <summary>
    /// <b>20 本のトリガが同じ条件を持つ</b>（表名・列・呼び名・上限だけが違う）。
    /// </summary>
    /// <remarks>
    /// <para><b>上限の数だけを見ても足りない。</b> <c>typeof</c> の枝や NUL の枝を 1 本消しても、
    /// <b>数の突き合わせは緑のまま</b>である——同じ規則を 10 列へ写すときに落ちるのはそこである
    /// （qa/03 の L-37 の型。<c>MasterCodeGuardTests.トリガ12本は同じ条件を持つ</c> と同じ作法）。
    /// <b>012（マスタと取引先）と 013（伝票）をまたいで突き合わせる</b>——
    /// <b>ファイルが分かれた瞬間に骨格がずれる</b>のが、この型のいちばん多い起き方である。</para>
    /// <para><b>表名・列・呼び名・上限を伏せてから比べる。</b> 残るのが骨格である。</para>
    /// </remarks>
    [Fact]
    public void 文字の欄のトリガは母数のぶんだけ同じ条件を持つ()
    {
        using var db = SchemaSeed.Create();
        var shapes = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var row in TextLengthFields)
        {
            var (table, column, max) = ((string)row[1], (string)row[3], (int)row[4]);
            foreach (var kind in new[] { "insert", "update" })
            {
                var name = $"trg_{table}_{column}_length_{kind}";
                var definition = TestDatabase.ScalarOf<string>(
                    db, $"SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = '{name}'");

                Assert.False(string.IsNullOrEmpty(definition), $"{name} が無い");

                // 表・列・上限・呼び名を伏せる。**伏せたあとに残るのが条件の骨格**である。
                var shape = definition
                    .Replace(table, "<表>", StringComparison.Ordinal)
                    .Replace(column, "<列>", StringComparison.Ordinal)
                    .Replace(max.ToString(CultureInfo.InvariantCulture), "<上限>", StringComparison.Ordinal);
                shape = Regex.Replace(shape, "'「[^」]+」[^']*'", "'<文言>'");

                if (!shapes.TryGetValue(kind, out var found))
                {
                    found = [];
                    shapes[kind] = found;
                }

                found.Add(shape);
            }
        }

        foreach (var (kind, found) in shapes)
        {
            Assert.Equal(TextLengthFields.Count(), found.Count);
            Assert.All(found, shape => Assert.True(shape == found[0], $"{kind} のトリガの骨格が揃っていない"));

            // **骨格に何が入っていなければならないか**を名指しで表明する
            // （全部が同じでも、全部から同じ枝が抜けていれば揃ってはいる）。
            Assert.Contains("instr(CAST(NEW.<列> AS BLOB), x'00') > 0", found[0], StringComparison.Ordinal);
            Assert.Contains("typeof(NEW.<列>) = 'blob'", found[0], StringComparison.Ordinal);
            Assert.Contains("LENGTH(CAST(NEW.<列> AS BLOB)) > 4 * LENGTH(NEW.<列>)", found[0], StringComparison.Ordinal);
            Assert.Contains("LENGTH(NEW.<列>) > <上限>", found[0], StringComparison.Ordinal);
        }
    }

    /// <summary>デザインの 1 つの欄。</summary>
    /// <remarks>
    /// <b>JSON を読んだまま返す。</b> 呼ぶ側が見たい属性を選ぶ——
    /// 「<c>MaxLength</c> が無いこと」と「プレースホルダに数があること」を両方表明する。
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
}
