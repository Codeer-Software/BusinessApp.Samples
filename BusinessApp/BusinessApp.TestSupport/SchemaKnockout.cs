namespace BusinessApp.TestSupport;

using System.Text;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

/// <summary>外す制約の種類。</summary>
public enum KnockoutKind
{
    /// <summary><c>sqlite_master</c> に独立した行を持つ。<c>DROP TRIGGER</c> で外す。</summary>
    Trigger,

    /// <summary><c>sqlite_master</c> に独立した行を持つ。<c>DROP INDEX</c> で外す。</summary>
    Index,

    /// <summary>表定義の中。DDL のテキストから除いて組み直す。</summary>
    Check,

    /// <summary>表定義の中（表レベルの <c>UNIQUE (…)</c> と、列に付いた <c>UNIQUE</c> の両方）。</summary>
    Unique,

    /// <summary>表定義の中。この DDL では列に付いた <c>REFERENCES …</c> だけである。</summary>
    ForeignKey,
}

/// <summary>外す点 1 つ。<see cref="Name"/> が掃引の識別子になる。</summary>
/// <param name="Name">外す点の名前。<c>trigger:…</c> / <c>check:&lt;表&gt;:&lt;通番&gt;</c> の形。</param>
/// <param name="Kind">種類。</param>
/// <param name="Where">どこにあるか（表の名前。トリガとインデックスは自分の名前）。</param>
/// <param name="Text">外される字面。報告に出すためだけに持つ。</param>
public sealed record KnockoutPoint(string Name, KnockoutKind Kind, string Where, string Text);

/// <summary>
/// <c>Designer/ddl/</c> の制約を <b>1 つだけ</b>外したスキーマを作る（ADR-0053）。
/// </summary>
/// <remarks>
/// <para><b>これは計器であって、守りではない。</b> 外した DB でテスト群を回し、
/// <b>赤にならなかった制約＝誰もテストしていない制約</b>を報告するために使う
/// （<c>docs/qa/05_観点網羅の計器.md</c> §4）。</para>
/// <para><b>列挙の出どころは対象ごとに違う</b>（ADR-0053 決定 1）。トリガとインデックスは
/// <c>sqlite_master</c> に独立した行を持つので <b>SQLite 自身が解析した結果</b>を読む。
/// 表の中の制約は表定義の 1 本の文字列にしか現れないので、DDL のテキストを解析する。
/// <b>両者の本数を突き合わせる番人</b>を <c>SchemaKnockoutTests</c> が持つ。</para>
/// <para><b>既定では何も起きない。</b> <see cref="EnvironmentVariable"/> が設定されているときだけ
/// <see cref="TestDatabase.Create"/> が外す（ADR-0053 決定 4）。</para>
/// </remarks>
public static class SchemaKnockout
{
    /// <summary>外す点の名前を渡す環境変数。掃引の駆動役が 1 点ずつ設定する。</summary>
    public const string EnvironmentVariable = "SCHEMA_KNOCKOUT";

    /// <summary>
    /// いま外すよう求められている点の名前。設定が無ければ <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b>空文字は「設定が無い」と同じに扱う。</b> シェルによっては消したつもりの変数が
    /// 空文字で残るので、そこで「名前が空の点は無い」と投げると掃引の後の通常のテストが全部落ちる。
    /// </remarks>
    public static string? Requested
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    /// <summary>
    /// <b>殺し手に数えないテスト</b>と、その理由（ADR-0053 決定 2）。
    /// </summary>
    /// <remarks>
    /// <b>スキーマの字面を読んで比べるテストは、制約を外した瞬間に必ず赤くなる。</b>
    /// 数えてしまうと全制約が「見張られている」ことになり、<b>計器が常に緑を返す</b>——
    /// 計器そのものの静かな失敗である。
    /// <b>ここに書いたクラスが実在することは <c>SchemaKnockoutTests</c> が検査する</b>
    /// （名前を変えた日に、除外だけが古い名前を指して黙って無効になるのを止める）。
    /// </remarks>
    public static IReadOnlyDictionary<string, string> TestsThatReadSchemaText { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MigrationEquivalenceTests"] =
                "Designer/ddl/ と baseline + migrations を突き合わせる。ddl 側だけを外すので必ず差が出る",
            ["EnumConsistencyTests"] =
                "CHECK の中身から区分値を読み取る。CHECK を外せば読み取れなくなる",
            ["FieldLengthConsistencyTests"] =
                "sqlite_master のトリガ本文と表定義から桁の規則を読み取る",
            ["SchemaKnockoutTests"] =
                "計器そのもののテスト。外した点を自分で組み立てて確かめるので、見張りの証拠にならない",
            ["SchemaShapeTests"] =
                "sqlite_master と pragma_* でスキーマの形を読む。計上済みを守るトリガの名前の実在を表明しており、"
                + "どれを外しても振る舞いと無関係に赤くなる",
            ["TextLengthGuardTests.トリガは母数のぶんだけある"] =
                "文字の欄のトリガの名前と本数を sqlite_master から数える（数はここに書かない）。"
                + "どれを外しても振る舞いと無関係に赤くなる",
            ["MasterCodeGuardTests.コードを持つ6つの表すべてに追加と更新のトリガがある"] =
                "code_format のトリガ 12 本の名前の実在を表明する",
            ["MasterCodeGuardTests.トリガ12本は同じ条件を持つ"] = "トリガの定義文を読む",
            ["MasterCodeGuardTests.WHEN節と本文の条件は同値である"] = "トリガの定義文を読む",
            ["MasterCodeGuardTests.断りの文言は書式の説明を含む"] = "トリガの定義文を読む",
            ["MasterCodeGuardTests.トリガが通す字は関門が通す字と過不足なく一致する"] = "トリガの WHEN 節を読む",
            ["MasterCodeGuardTests.形の規則もトリガと関門で一致する"] = "トリガの WHEN 節を読む",
            ["JournalDescriptionGuardTests.トリガが空とみなす字は_char_IsWhiteSpace_と過不足なく一致する"] =
                "トリガの定義文を読む",
            ["DateFormatGuardTests.日付の列を持つ表すべてに追加と更新のトリガがある"] =
                "date_format のトリガの名前と本数を sqlite_master から数える（数はここに書かない）",
            ["DateFormatGuardTests.DATEで宣言した列は1つ残らず見張られている"] =
                "トリガの定義文から date(NEW.…) の列名を拾う",
            ["DateFormatGuardTests.条件はどこも同じ字である"] = "トリガの定義文を読む",
            ["TaxRateConstraintTests.税率区分の値は税区分マスタと同じ2つである"] =
                "2 つの表の rate_kind の CHECK を定義文から読んで突き合わせる。どちらを外しても読めなくなる",
        };

    /// <summary>
    /// テストのソースを走査して、<b>スキーマの字面を読んでいるのに除外表に無いもの</b>を挙げる。
    /// </summary>
    /// <remarks>
    /// <para><b>除外表は、足し忘れたときに黙って壊れる。</b> 足し忘れたぶんだけ
    /// 「振る舞いを 1 つも検査していないのに殺せた」が増え、<b>計器は最良の報告を返す</b>——
    /// 実際に 2026-09-13 の初回掃引で、トリガ 54 点のうち 38 点がこれで水増しされていた
    /// （qa/02 のラウンド 88）。</para>
    /// <para><b>コメントは剥がしてから見る。</b> 「sqlite_master から読んで貼り直す」のような
    /// 説明文まで拾うと、字面を読んでいないテストまで外すことになる。</para>
    /// <para><b>同じクラスの中の呼び出しは追う。</b> <c>Definition()</c> のような助けを
    /// 経由して読んでいるテストが実在する。</para>
    /// </remarks>
    public static IReadOnlyList<string> TestsReadingSchemaTextOutsideTheTable(string testsDirectory)
        => TestsReadingSchemaTextOutsideTheTable(testsDirectory, TestsThatReadSchemaText);

    /// <summary>
    /// 除外表を差し替えて同じ走査をする。<b>「0 件は緑ではない」を確かめるため</b>に公開している——
    /// 空の表を渡して何も挙がらなければ、走査そのものが死んでいる。
    /// </summary>
    public static IReadOnlyList<string> TestsReadingSchemaTextOutsideTheTable(
        string testsDirectory, IReadOnlyDictionary<string, string> table)
    {
        var uncovered = new List<string>();
        foreach (var file in Directory.EnumerateFiles(testsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var source = StripComments(File.ReadAllText(file));
            var type = TypeName.Match(source);
            if (!type.Success)
            {
                continue;
            }

            var members = MembersOf(source);
            var reading = members
                .Where(member => SchemaTextRead.IsMatch(member.Value))
                .Select(member => member.Key)
                .ToHashSet(StringComparer.Ordinal);

            // 同じクラスの中の呼び出しを、増えなくなるまで追う。
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var member in members)
                {
                    if (!reading.Contains(member.Key)
                        && reading.Any(name => Regex.IsMatch(member.Value, @"\b" + Regex.Escape(name) + @"\s*\(")))
                    {
                        reading.Add(member.Key);
                        grew = true;
                    }
                }
            }

            // **挙げるのは公開メンバだけである。** 助けの private メソッドは
            // filter で外せる単位ではなく、**それを呼ぶテストのほうが外す対象**だから。
            uncovered.AddRange(reading
                .Where(name => members[name].TrimStart().StartsWith("public", StringComparison.Ordinal))
                .Select(name => $"{type.Groups[1].Value}.{name}")
                .Where(full => !table.Keys.Any(key => full.Contains(key, StringComparison.Ordinal)))
                .OrderBy(full => full, StringComparer.Ordinal));
        }

        return uncovered;
    }

    private static readonly Regex TypeName = new(@"^public\s+(?:sealed\s+)?class\s+(\w+)", RegexOptions.Multiline);
    private static readonly Regex SchemaTextRead = new("sqlite_master|pragma_", RegexOptions.IgnoreCase);
    private static readonly Regex MemberStart =
        new(@"^    (?:public|private|internal|protected)[^\r\n=;]*?\b(\w+)\s*\(", RegexOptions.Multiline);

    /// <summary>クラスの直下のメンバを「名前 → 本文」で切り出す（次のメンバの手前まで）。</summary>
    private static Dictionary<string, string> MembersOf(string source)
    {
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        var starts = MemberStart.Matches(source);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : source.Length;
            var name = starts[i].Groups[1].Value;
            members[name] = members.TryGetValue(name, out var already)
                ? already + source[starts[i].Index..end]
                : source[starts[i].Index..end];
        }

        return members;
    }

    private static string StripComments(string source)
        => Regex.Replace(source, @"//[^\r\n]*|/\*.*?\*/", " ", RegexOptions.Singleline);

    /// <summary>掃引で使う <c>dotnet test --filter</c> の式。</summary>
    public static string TestFilter()
        => string.Join(" & ", TestsThatReadSchemaText.Keys.Select(name => $"FullyQualifiedName!~{name}"));

    /// <summary>外せる点をすべて数える。</summary>
    /// <remarks>
    /// <b>1 度数えたら覚えておく。</b> 掃引は点の数だけ <see cref="CreateWithout"/> を呼び、
    /// その都度ここを通るので、<b>覚えないと DDL を 2 倍流すことになる</b>
    /// （テスト用の DB を 1 つ作るたびに、数えるためのフル DB がもう 1 つ立つ）。
    /// DDL はプロセスの途中で変わらない。
    /// </remarks>
    public static IReadOnlyList<KnockoutPoint> All() => Counted.Value;

    private static readonly Lazy<IReadOnlyList<KnockoutPoint>> Counted = new(CountAll);

    private static IReadOnlyList<KnockoutPoint> CountAll()
    {
        var points = new List<KnockoutPoint>();

        using (var db = TestDatabase.CreateFromFiles(TestDatabase.DdlFiles()))
        {
            foreach (var name in TestDatabase.Query(
                         db, "SELECT name FROM sqlite_master WHERE type = 'trigger' AND sql IS NOT NULL ORDER BY name"))
            {
                points.Add(new KnockoutPoint($"trigger:{name}", KnockoutKind.Trigger, name, name));
            }

            // **一意でないインデックスは制約ではない。** 外しても振る舞いは 1 つも変わらず、
            // 遅くなるだけなので、**どんなテストを書いても殺せない**（2026-09-13 の掃引で
            // `ix_` の 9 本が全部生き残った）。数えると「永久に殺せない生き残り」が溜まり、
            // ADR-0012 §4 が避けよと言う許容リストが要るようになる。
            // **速さを見張る計器はこれとは別物**である（まだ無い）。
            foreach (var name in TestDatabase.Query(
                         db,
                         "SELECT name FROM sqlite_master WHERE type = 'index'"
                         + " AND sql LIKE 'CREATE UNIQUE INDEX%' ORDER BY name"))
            {
                points.Add(new KnockoutPoint($"index:{name}", KnockoutKind.Index, name, name));
            }
        }

        points.AddRange(TableConstraints());
        return points;
    }

    /// <summary>1 点だけ外した DDL でインメモリ DB を作る。</summary>
    public static SqliteConnection CreateWithout(string name)
    {
        var point = All().SingleOrDefault(p => p.Name == name)
            ?? throw new InvalidOperationException(
                $"外す点 {name} は無い。{EnvironmentVariable} の値を確かめること。");

        if (point.Kind is KnockoutKind.Trigger or KnockoutKind.Index)
        {
            var db = TestDatabase.CreateFromFiles(TestDatabase.DdlFiles());
            var keyword = point.Kind == KnockoutKind.Trigger ? "TRIGGER" : "INDEX";
            TestDatabase.Execute(db, $"DROP {keyword} {point.Where}");
            return db;
        }

        return TestDatabase.CreateFromSql(DdlWithout(point));
    }

    /// <summary>表の中の制約を 1 つ除いた DDL の本文（ファイルの順に並ぶ）。</summary>
    public static IReadOnlyList<string> DdlWithout(KnockoutPoint point)
    {
        var texts = new List<string>();
        var cut = false;

        foreach (var file in TestDatabase.DdlFiles())
        {
            var sql = File.ReadAllText(file);
            var without = Without(sql, point);
            if (without is null)
            {
                texts.Add(sql);
                continue;
            }

            // **同じ名前の点が 2 つのファイルに当たったら止める。** 静かに 2 か所を外すと、
            // 「1 点だけ外す」という計器の前提が崩れたまま緑になる。
            if (cut)
            {
                throw new InvalidOperationException($"外す点 {point.Name} が 2 つのファイルに当たった。");
            }

            cut = true;
            texts.Add(without);
        }

        return cut
            ? texts
            : throw new InvalidOperationException($"外す点 {point.Name} を DDL の中に見つけられない。");
    }

    /// <summary>この 1 ファイルの本文から点を 1 つ除く。当たらなければ <c>null</c>。</summary>
    /// <remarks>
    /// <b>公開しているのは検体のためである</b>——切った跡が SQL として流せるかは、
    /// 実物の DDL だけでは確かめきれない（先頭の要素が表レベルの制約、という形がいまは無い）。
    /// </remarks>
    public static string? Without(string sql, KnockoutPoint point)
    {
        var span = CutOf(sql, point);
        return span is null ? null : sql.Remove(span.Value.Start, span.Value.Length);
    }

    // ---- 表の中の制約を数える ----

    private static IEnumerable<KnockoutPoint> TableConstraints()
    {
        foreach (var file in TestDatabase.DdlFiles())
        {
            foreach (var point in TableConstraintsIn(File.ReadAllText(file)))
            {
                yield return point;
            }
        }
    }

    /// <summary>1 ファイル分の表定義から、外せる制約を数える。</summary>
    /// <remarks>
    /// <b>公開しているのは検体のためである</b>——実物の DDL だけで確かめると、
    /// 書き方が増えた日に「拾えていない」ことが緑のまま通る。
    /// </remarks>
    public static IReadOnlyList<KnockoutPoint> TableConstraintsIn(string sql)
    {
        RejectUnsupportedForms(sql);

        var masked = Mask(sql);
        var points = new List<KnockoutPoint>();

        foreach (var (table, bodyStart, bodyEnd) in Tables(masked))
        {
            var counts = new Dictionary<KnockoutKind, int>();
            foreach (var (partStart, partEnd) in Parts(masked, bodyStart, bodyEnd))
            {
                foreach (var (kind, span) in ConstraintsInPart(masked, partStart, partEnd, bodyStart, bodyEnd))
                {
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    points.Add(new KnockoutPoint(
                        NameOf(kind, table, counts[kind]),
                        kind,
                        table,
                        sql.Substring(span.Start, span.Length).Trim()));
                }
            }
        }

        return points;
    }

    /// <summary>
    /// この走査が扱えない書き方が DDL に入ったら止める。
    /// </summary>
    /// <remarks>
    /// <b>黙って取りこぼすより、足した日に止まるほうがよい。</b> いまの <c>Designer/ddl/</c> には
    /// 名前つき制約も表レベルの <c>FOREIGN KEY</c> も <c>ON DELETE</c> も 1 つも無い
    /// （2026-09-13 実測）。入ったら、切り出しの規則を決め直すこと。
    /// </remarks>
    private static void RejectUnsupportedForms(string sql)
    {
        var masked = Mask(sql);
        foreach (var (name, pattern) in UnsupportedForms)
        {
            // **語の境界で見る。** 素の部分一致だと、`BEFORE INSERT ON delete_log` のように
            // 表名が語で始まるだけで誤爆する。
            if (Regex.IsMatch(masked, pattern, RegexOptions.IgnoreCase))
            {
                throw new NotSupportedException(
                    $"DDL に「{name}」が入った。SchemaKnockout はこの形の切り出しを決めていない（ADR-0053 決定 1）。");
            }
        }
    }

    private static readonly (string Name, string Pattern)[] UnsupportedForms =
    [
        ("FOREIGN KEY", @"\bFOREIGN\s+KEY\b"),
        ("CONSTRAINT", @"\bCONSTRAINT\s+\w"),
        ("ON DELETE", @"\bON\s+DELETE\b"),
        ("ON UPDATE", @"\bON\s+UPDATE\b"),
        ("DEFERRABLE", @"\bDEFERRABLE\b"),
    ];

    private static IEnumerable<(KnockoutKind Kind, Span Span)> ConstraintsInPart(
        string masked, int start, int end, int bodyStart, int bodyEnd)
    {
        var head = masked[start..end].TrimStart();
        var headStart = start + (masked[start..end].Length - head.Length);

        // 表レベル（部分ぜんぶが制約）。**区切りのカンマごと落とす**——
        // 残すと「, )」や「( ,」になって DDL が流れなくなる。
        if (head.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) && AfterKeyword(head, "CHECK"))
        {
            yield return (KnockoutKind.Check, WithSeparator(start, end, bodyStart, bodyEnd));
            yield break;
        }

        if (head.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase) && AfterKeyword(head, "UNIQUE"))
        {
            yield return (KnockoutKind.Unique, WithSeparator(start, end, bodyStart, bodyEnd));
            yield break;
        }

        // 列レベル（列定義の一部）。**その語だけを落とす。**
        // **1 つ見つけて止めない**——`x INTEGER CHECK (…) CHECK (…)` のように 2 本書ける。
        // 止めると、2 本目は数にも入らず外れもしないまま、誰にも気づかれない。
        for (var check = KeywordAt(masked, headStart, end, "CHECK"); check >= 0;)
        {
            var close = MatchingParen(masked, check);
            yield return (KnockoutKind.Check, new Span(check, close + 1 - check));
            check = KeywordAt(masked, close + 1, end, "CHECK");
        }

        for (var unique = KeywordAt(masked, headStart, end, "UNIQUE"); unique >= 0;)
        {
            yield return (KnockoutKind.Unique, new Span(unique, "UNIQUE".Length));
            unique = KeywordAt(masked, unique + "UNIQUE".Length, end, "UNIQUE");
        }

        var references = KeywordAt(masked, headStart, end, "REFERENCES");
        if (references >= 0)
        {
            // **参照先の括弧までで切る。部分の末尾まで切らない。**
            // 末尾まで切ると、同じ列に続く CHECK を巻き込む——`Designer/ddl/004_masters.sql` の
            // `parent_partner_id INTEGER REFERENCES partners(id) CHECK (…)` で実際に起きていた
            // （2026-09-13。qa/02 のラウンド 88）。**1 点だけ外すという前提が黙って破れる。**
            yield return (KnockoutKind.ForeignKey, new Span(references, MatchingParen(masked, references) + 1 - references));
        }
    }

    /// <summary><c>CHECK</c> / <c>UNIQUE</c> の直後が「(」なら表レベルの制約である。</summary>
    private static bool AfterKeyword(string head, string keyword)
        => head[keyword.Length..].TrimStart().StartsWith('(');

    /// <summary>表レベルの制約を、区切りのカンマごと切り出す。</summary>
    /// <remarks>
    /// <b>カンマを探して字面を遡らない。</b> <c>Parts</c> が切った位置がそのまま答えである——
    /// 先頭の要素でなければ直前が区切りのカンマ、先頭なら直後が区切りのカンマである。
    /// 遡って探すと、<b>先頭の要素だったときに表の外まで走って前の文を切る</b>
    /// （いまの DDL では先頭が必ず列なので起きないが、起きたときに静かに壊れる形である）。
    /// </remarks>
    private static Span WithSeparator(int start, int end, int bodyStart, int bodyEnd)
    {
        if (start > bodyStart)
        {
            return new Span(start - 1, end - (start - 1));
        }

        return end < bodyEnd ? new Span(start, end + 1 - start) : new Span(start, end - start);
    }

    private static int KeywordAt(string masked, int start, int end, string keyword)
    {
        for (var i = start; i + keyword.Length <= end; i++)
        {
            if (string.Compare(masked, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                continue;
            }

            var before = i == 0 || !char.IsLetterOrDigit(masked[i - 1]) && masked[i - 1] != '_';
            var after = i + keyword.Length >= masked.Length
                || !char.IsLetterOrDigit(masked[i + keyword.Length]) && masked[i + keyword.Length] != '_';
            if (before && after)
            {
                return i;
            }
        }

        return -1;
    }

    private static int MatchingParen(string masked, int from)
    {
        var open = masked.IndexOf('(', from);
        var depth = 0;
        for (var i = open; i < masked.Length; i++)
        {
            if (masked[i] == '(')
            {
                depth++;
            }
            else if (masked[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        throw new InvalidOperationException("括弧の対応が取れない DDL がある。");
    }

    private static IEnumerable<(string Table, int Start, int End)> Tables(string masked)
    {
        var at = 0;
        while (true)
        {
            var keyword = KeywordAt(masked, at, masked.Length, "CREATE");
            if (keyword < 0)
            {
                yield break;
            }

            at = keyword + "CREATE".Length;
            var rest = masked[at..];
            var trimmed = rest.TrimStart();
            if (!trimmed.StartsWith("TABLE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var nameStart = at + (rest.Length - trimmed.Length) + "TABLE".Length;
            var open = masked.IndexOf('(', nameStart);
            var table = masked[nameStart..open].Trim();
            var close = MatchingParen(masked, nameStart);
            yield return (table, open + 1, close);
            at = close;
        }
    }

    /// <summary>表定義の本文を、いちばん外側のカンマで割る。</summary>
    private static IEnumerable<(int Start, int End)> Parts(string masked, int start, int end)
    {
        var depth = 0;
        var from = start;
        for (var i = start; i < end; i++)
        {
            if (masked[i] == '(')
            {
                depth++;
            }
            else if (masked[i] == ')')
            {
                depth--;
            }
            else if (masked[i] == ',' && depth == 0)
            {
                if (!string.IsNullOrWhiteSpace(masked[from..i]))
                {
                    yield return (from, i);
                }

                from = i + 1;
            }
        }

        if (!string.IsNullOrWhiteSpace(masked[from..end]))
        {
            yield return (from, end);
        }
    }

    /// <summary>
    /// コメントと文字列リテラルを<b>同じ長さの空白</b>に置き換えた写しを返す。
    /// </summary>
    /// <remarks>
    /// <b>長さを保つのが肝である。</b> 消してしまうと元の字面の位置がずれ、
    /// <b>切り出す場所が黙って 1 文字ずつずれる</b>。切るのは元の字面、数えるのはこの写し。
    /// 文字列リテラルまで潰すのは、<c>CHECK (code &lt;&gt; '(')</c> のような値で
    /// 括弧の数え上げが狂わないようにするため。
    /// </remarks>
    public static string Mask(string sql)
    {
        var masked = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                var end = i + 1;
                while (end < sql.Length)
                {
                    if (sql[end] != '\'')
                    {
                        end++;
                    }
                    else if (end + 1 < sql.Length && sql[end + 1] == '\'')
                    {
                        end += 2;
                    }
                    else
                    {
                        break;
                    }
                }

                // **閉じない引用符でも長さを変えない。** 閉じの 1 文字を足すと写しが伸び、
                // 以降の切り出しが全部ずれる（ずれは静かに別の場所を切る形で出る）。
                var closed = end < sql.Length;
                masked.Append('\'').Append(Blanks(sql, i + 1, closed ? end : sql.Length));
                if (closed)
                {
                    masked.Append('\'');
                }

                i = closed ? end + 1 : sql.Length;
            }
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                masked.Append(Blanks(sql, i, end));
                i = end;
            }
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i, StringComparison.Ordinal);
                end = end < 0 ? sql.Length : end + 2;
                masked.Append(Blanks(sql, i, end));
                i = end;
            }
            else
            {
                masked.Append(sql[i]);
                i++;
            }
        }

        return masked.ToString();
    }

    /// <summary>改行だけ残して空白に潰す（行番号を保つため）。</summary>
    private static string Blanks(string sql, int start, int end)
    {
        var blanks = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            blanks.Append(sql[i] == '\n' ? '\n' : ' ');
        }

        return blanks.ToString();
    }

    private static Span? CutOf(string sql, KnockoutPoint point)
    {
        var masked = Mask(sql);
        foreach (var (table, bodyStart, bodyEnd) in Tables(masked))
        {
            if (table != point.Where)
            {
                continue;
            }

            var counts = 0;
            foreach (var (partStart, partEnd) in Parts(masked, bodyStart, bodyEnd))
            {
                foreach (var (kind, span) in ConstraintsInPart(masked, partStart, partEnd, bodyStart, bodyEnd))
                {
                    if (kind != point.Kind)
                    {
                        continue;
                    }

                    counts++;
                    if (NameOf(kind, table, counts) == point.Name)
                    {
                        return span;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>外す点の名前。<b>この 1 か所だけが名前を組み立てる</b>——数える側と切る側で形がずれると、
    /// 「見つけられない」ではなく<b>別の制約を外したまま緑になる</b>。</summary>
    private static string NameOf(KnockoutKind kind, string table, int ordinal)
        => $"{kind.ToString().ToLowerInvariant()}:{table}:{ordinal}";

    private readonly record struct Span(int Start, int Length);
}
