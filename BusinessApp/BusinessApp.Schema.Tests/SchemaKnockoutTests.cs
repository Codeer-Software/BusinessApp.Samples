namespace BusinessApp.Schema.Tests;

using System.Reflection;

using BusinessApp.TestSupport;

/// <summary>
/// 制約ノックアウトの計器そのものの検査（ADR-0053）。
/// </summary>
/// <remarks>
/// <para><b>計器は静かに失敗する。</b> 走査が制約を取りこぼしても、外し方が何も変えなくても、
/// 掃引はただ「全部殺せた」と報告する——<b>0 点の掃引は緑ではない</b>。
/// <c>SchemaSnapshotTests</c> が比較器そのものを固定しているのと同じ役目である。</para>
/// <para><b>このクラスは掃引の殺し手から外してある</b>（<c>SchemaKnockout.TestsThatReadSchemaText</c>）。
/// 外した点を直接組み立てて確かめるので、掃引中は必ず赤か緑に固定されてしまい、
/// <b>「その制約を見張っているテストがある」の証拠にならない</b>。</para>
/// </remarks>
public class SchemaKnockoutTests
{
    // ---- 走査（Mask・列挙） ----

    [Theory]
    [InlineData("a -- コメント\nb")]
    [InlineData("a /* コメント */ b")]
    [InlineData("CHECK (v IN ('あ', 'い'))")]
    public void 写しは元の字面と同じ長さである(string sql)
    {
        Assert.Equal(sql.Length, SchemaKnockout.Mask(sql).Length);
    }

    /// <summary>
    /// 長さが変わると<b>切り出す場所が黙ってずれる</b>。行番号も保つので、改行は残す。
    /// </summary>
    [Fact]
    public void 写しはコメントと文字列の中身を空白にし改行だけ残す()
    {
        Assert.Equal("a        \nb", SchemaKnockout.Mask("a -- コメント\nb"));
        Assert.Equal("v = '    '", SchemaKnockout.Mask("v = 'あいうえ'"));
        Assert.Equal("a   \n   b", SchemaKnockout.Mask("a /*\n*/ b"));
    }

    /// <summary>
    /// コメントと文字列リテラルの中の「CHECK」「(」「,」を制約と数えたら、
    /// <b>存在しない点を外そうとして掃引が止まる</b>か、<b>別の場所を切る</b>。
    /// </summary>
    [Fact]
    public void コメントと文字列の中身は制約として数えない()
    {
        var points = SchemaKnockout.TableConstraintsIn("""
            CREATE TABLE t (
                -- CHECK (これはコメント), UNIQUE (これも)
                v TEXT NOT NULL DEFAULT '(, CHECK (' /* UNIQUE ( */
            );
            """);

        Assert.Empty(points);
    }

    [Fact]
    public void 表レベルと列レベルの制約をどちらも数える()
    {
        var points = SchemaKnockout.TableConstraintsIn("""
            CREATE TABLE t (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                code    TEXT NOT NULL UNIQUE,
                owner   INTEGER NOT NULL REFERENCES other(id),
                status  TEXT NOT NULL CHECK (status IN ('open', 'closed')),

                CHECK (id > 0),
                UNIQUE (code, owner)
            );
            """);

        Assert.Equal(
            ["unique:t:1", "foreignkey:t:1", "check:t:1", "check:t:2", "unique:t:2"],
            points.Select(p => p.Name));
        Assert.Equal(
            [KnockoutKind.Unique, KnockoutKind.ForeignKey, KnockoutKind.Check, KnockoutKind.Check, KnockoutKind.Unique],
            points.Select(p => p.Kind));
    }

    /// <summary>
    /// <b>数える側と切る側で名前の組み立てがずれると、別の制約を外したまま緑になる。</b>
    /// 名前を作るのは 1 か所だけ（<c>NameOf</c>）で、ここはその往復を固定する。
    /// </summary>
    [Fact]
    public void 数えた点はその名前で切り出せる()
    {
        foreach (var point in SchemaKnockout.All().Where(p => p.Kind is not (KnockoutKind.Trigger or KnockoutKind.Index)))
        {
            var after = SchemaKnockout.DdlWithout(point);
            var before = TestDatabase.DdlFiles().Select(File.ReadAllText).ToList();

            Assert.Equal(before.Count, after.Count);
            Assert.Equal(1, before.Zip(after).Count(pair => pair.First != pair.Second));
        }
    }

    /// <summary>
    /// <b>切った跡が SQL として流せること。</b> 区切りのカンマを残すと「, )」や「( ,」になって
    /// DDL が流れなくなり、掃引はそれを「殺せた」と数えてしまう（ADR-0053 決定 5 の受け皿は
    /// <b>他の制約が依存していた場合</b>のためにあるのであって、切り方の粗さを隠すためではない）。
    /// <b>表レベルの制約が先頭・真ん中・末尾のどこにあっても</b>確かめる。
    /// </summary>
    [Theory]
    // 先頭（いまの DDL には無い形。だから検体で持つ）
    [InlineData("CREATE TABLE t (\n  CHECK (1 > 0),\n  a INTEGER NOT NULL,\n  b TEXT\n);", "check:t:1")]
    // 真ん中
    [InlineData("CREATE TABLE t (\n  a INTEGER NOT NULL,\n  UNIQUE (a),\n  b TEXT\n);", "unique:t:1")]
    // 末尾
    [InlineData("CREATE TABLE t (\n  a INTEGER NOT NULL,\n  b TEXT,\n  CHECK (a > 0)\n);", "check:t:1")]
    // それ 1 つしか要素が無い（退化した形）
    [InlineData("CREATE TABLE u (\n  b TEXT NOT NULL UNIQUE\n);", "unique:u:1")]
    // 列レベルの CHECK
    [InlineData("CREATE TABLE t (\n  a INTEGER NOT NULL CHECK (a > 0),\n  b TEXT\n);", "check:t:1")]
    // 列レベルの UNIQUE
    [InlineData("CREATE TABLE t (\n  a INTEGER,\n  b TEXT NOT NULL UNIQUE\n);", "unique:t:1")]
    // 列レベルの外部キー
    [InlineData("CREATE TABLE u (\n  id INTEGER PRIMARY KEY\n);\nCREATE TABLE t (\n  a INTEGER NOT NULL REFERENCES u(id)\n);", "foreignkey:t:1")]
    public void 切った跡はSQLとして流せる(string sql, string name)
    {
        var point = Assert.Single(SchemaKnockout.TableConstraintsIn(sql), p => p.Name == name);
        var without = SchemaKnockout.Without(sql, point)
            ?? throw new InvalidOperationException($"{name} を数えたのに切り出せない");

        Assert.NotEqual(sql, without);

        using var db = TestDatabase.CreateFromSql([without]);

        Assert.NotEmpty(TestDatabase.Query(db, "SELECT name FROM sqlite_master WHERE type = 'table'"));
    }

    /// <summary>
    /// 走査が扱えない書き方を足した日に<b>止まる</b>こと。黙って取りこぼすほうが悪い。
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE t (a INTEGER, FOREIGN KEY (a) REFERENCES u(id));", "FOREIGN KEY")]
    [InlineData("CREATE TABLE t (a INTEGER CONSTRAINT c CHECK (a > 0));", "CONSTRAINT")]
    [InlineData("CREATE TABLE t (a INTEGER REFERENCES u(id) ON DELETE CASCADE);", "ON DELETE")]
    [InlineData("CREATE TABLE t (a INTEGER REFERENCES u(id) ON UPDATE CASCADE);", "ON UPDATE")]
    [InlineData("CREATE TABLE t (a INTEGER REFERENCES u(id) DEFERRABLE INITIALLY DEFERRED);", "DEFERRABLE")]
    public void 決めていない書き方は投げる(string sql, string form)
    {
        // **どの語で止まったかまで見る。** 型だけだと、検体の中の別の語に当たっても緑になり、
        // **語の表から 1 行消しても気づけない**（qa/03 の L-45 と同じ形）。
        var thrown = Assert.Throws<NotSupportedException>(() => SchemaKnockout.TableConstraintsIn(sql));

        Assert.Contains(form, thrown.Message, StringComparison.Ordinal);
    }

    // ---- 正典との突き合わせ ----

    /// <summary>
    /// トリガとインデックスは <c>sqlite_master</c> から数えるが、<b>走査がそれを取りこぼしていないか</b>は
    /// テキストの側からも数えて突き合わせる。片方だけを信じると、<b>数え落としが緑のまま通る</b>。
    /// </summary>
    [Fact]
    public void トリガとインデックスの本数はDDLの字面と一致する()
    {
        var text = string.Join("\n", TestDatabase.DdlFiles().Select(File.ReadAllText).Select(SchemaKnockout.Mask));
        var points = SchemaKnockout.All();

        Assert.Equal(
            CountOf(text, "CREATE TRIGGER"),
            points.Count(p => p.Kind == KnockoutKind.Trigger));
        // **一意でないインデックスは数えない**（制約ではないので外しても振る舞いが変わらない。SchemaKnockout）。
        Assert.Equal(
            CountOf(text, "CREATE UNIQUE INDEX"),
            points.Count(p => p.Kind == KnockoutKind.Index));
        Assert.True(CountOf(text, "CREATE INDEX") > 0, "一意でないインデックスが 1 本も無い（この検体の前提が崩れている）");
    }

    /// <summary>
    /// <b>表の中の UNIQUE と外部キーの数を、SQLite 自身の数え方と突き合わせる。</b>
    /// </summary>
    /// <remarks>
    /// <b>トリガとインデックスには突き合わせ先があったが、表の中の制約には無かった。</b>
    /// 自前のテキスト走査だけを信じると、<b>拾えていない書き方が黙って点から落ちる</b>——
    /// 落ちた制約は掃引に出てこないので、**「見張られていない」とすら報告されない**。
    /// <c>pragma_index_list</c> の <c>origin = 'u'</c> は表の <c>UNIQUE</c>（列に付けたものも表レベルのものも）、
    /// <c>pragma_foreign_key_list</c> は外部キーを、SQLite 自身の解析結果として返す。
    /// </remarks>
    [Fact]
    public void 表の中のUNIQUEと外部キーの数はSQLite自身の数え方と一致する()
    {
        using var db = TestDatabase.CreateFromFiles(TestDatabase.DdlFiles());
        var points = SchemaKnockout.All();
        var tables = TestDatabase.Query(
            db, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");

        Assert.NotEmpty(tables);
        foreach (var table in tables)
        {
            Assert.Equal(
                TestDatabase.ScalarOf<long>(
                    db, $"SELECT COUNT(*) FROM pragma_index_list('{table}') WHERE origin = 'u'"),
                points.Count(p => p.Kind == KnockoutKind.Unique && p.Where == table));
            Assert.Equal(
                TestDatabase.ScalarOf<long>(db, $"SELECT COUNT(*) FROM pragma_foreign_key_list('{table}')"),
                points.Count(p => p.Kind == KnockoutKind.ForeignKey && p.Where == table));
        }
    }

    /// <summary><b>0 点は緑ではない。</b> 走査が黙って何も返さなくなったらここで止まる。</summary>
    [Fact]
    public void 外す点は5種類とも1つ以上ある()
    {
        var byKind = SchemaKnockout.All().GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());

        foreach (var kind in Enum.GetValues<KnockoutKind>())
        {
            Assert.True(byKind.GetValueOrDefault(kind) > 0, $"{kind} の外す点が 1 つも無い");
        }
    }

    [Fact]
    public void 外す点の名前は重複しない()
    {
        var names = SchemaKnockout.All().Select(p => p.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- 外した結果 ----

    /// <summary>
    /// <b>何も変えない外し方があると、その点は必ず生き残る</b>——テストの穴ではなく計器の穴なのに、
    /// 報告には「誰もテストしていない制約」として出る。全点について、実際にスキーマが変わることを見る。
    /// </summary>
    [Fact]
    public void すべての外す点は実際にスキーマを変える()
    {
        using var full = TestDatabase.CreateFromFiles(TestDatabase.DdlFiles());
        var before = SchemaOf(full);

        foreach (var point in SchemaKnockout.All())
        {
            using var db = SchemaKnockout.CreateWithout(point.Name);

            Assert.True(SchemaOf(db) != before, $"{point.Name} を外してもスキーマが変わらない");
        }
    }

    /// <summary>
    /// <b>外した結果、点がちょうど 1 つだけ減ること。</b>
    /// </summary>
    /// <remarks>
    /// <b>切り出しが隣の制約を巻き込んでも、跡は SQL として流れるので誰も気づかない。</b>
    /// 実際に起きた——外部キーを「部分の末尾まで」切っていたので、
    /// <c>parent_partner_id INTEGER REFERENCES partners(id) CHECK (…)</c> で
    /// <b>CHECK まで一緒に消えていた</b>（2026-09-13。qa/02 のラウンド 88）。
    /// <c>切った跡はSQLとして流せる</c> も <c>すべての外す点は実際にスキーマを変える</c> も、
    /// どちらもこれを検出できない——<b>「1 点だけ外す」は、数えてはじめて確かめられる</b>。
    /// </remarks>
    [Fact]
    public void 表の中の制約を外すと点はちょうど1つだけ減る()
    {
        var before = Census(TestDatabase.DdlFiles().Select(File.ReadAllText));

        foreach (var point in SchemaKnockout.All().Where(p => p.Kind is not (KnockoutKind.Trigger or KnockoutKind.Index)))
        {
            var after = Census(SchemaKnockout.DdlWithout(point));

            foreach (var kind in Enum.GetValues<KnockoutKind>())
            {
                var expected = before.GetValueOrDefault(kind) - (kind == point.Kind ? 1 : 0);
                Assert.True(
                    after.GetValueOrDefault(kind) == expected,
                    $"{point.Name} を外したら {kind} が {before.GetValueOrDefault(kind)} → {after.GetValueOrDefault(kind)}（{expected} のはず）");
            }
        }
    }

    /// <summary>トリガとインデックスも、外すのは 1 本だけであること。</summary>
    [Fact]
    public void トリガとインデックスを外すと_sqlite_master_の行はちょうど1つ減る()
    {
        using var full = TestDatabase.CreateFromFiles(TestDatabase.DdlFiles());
        var before = TestDatabase.ScalarOf<long>(full, "SELECT COUNT(*) FROM sqlite_master");

        foreach (var point in SchemaKnockout.All().Where(p => p.Kind is KnockoutKind.Trigger or KnockoutKind.Index))
        {
            using var db = SchemaKnockout.CreateWithout(point.Name);

            Assert.Equal(before - 1, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM sqlite_master"));
        }
    }

    [Fact]
    public void 知らない名前の点は投げる()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SchemaKnockout.CreateWithout("check:no_such_table:1"));

        Assert.Contains(SchemaKnockout.EnvironmentVariable, e.Message, StringComparison.Ordinal);
    }

    // ---- 除外表 ----

    /// <summary>
    /// <b>クラス名を変えた日に、除外だけが古い名前を指して黙って無効になる</b>のを止める。
    /// 無効になった除外は、そのクラスを殺し手に数えることを意味し、<b>全制約が「見張られている」ことになる</b>。
    /// </summary>
    [Fact]
    public void 殺し手から外すテストは実在する()
    {
        var members = Assembly.GetExecutingAssembly().GetTypes()
            .SelectMany(type => type.GetMethods().Select(method => $"{type.Name}.{method.Name}").Append(type.Name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (name, reason) in SchemaKnockout.TestsThatReadSchemaText)
        {
            Assert.True(members.Contains(name), $"除外に書いた {name} がこのアセンブリに無い（理由: {reason}）");
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{name} の除外に理由が無い");
        }
    }

    /// <summary>
    /// <b>字面を読んでいるのに除外表に無いテストが無いこと。</b>
    /// </summary>
    /// <remarks>
    /// <b>除外表は片側しか守られていなかった。</b> 「表に書いた名前が実在するか」は見ていたが、
    /// 「表に無いのに字面を読んでいるテスト」は誰も見ていなかった——
    /// その結果、<b>トリガ 54 点のうち 38 点が、振る舞いを 1 つも検査していないのに
    /// 「殺せた」と数えられていた</b>（2026-09-13 の初回掃引。qa/02 のラウンド 88）。
    /// </remarks>
    [Fact]
    public void 字面を読むテストは全部除外表に載っている()
    {
        var uncovered = SchemaKnockout.TestsReadingSchemaTextOutsideTheTable(
            Path.Combine(TestDatabase.RepositoryDirectory, "BusinessApp", "BusinessApp.Schema.Tests"));

        Assert.True(
            uncovered.Count == 0,
            "スキーマの字面を読んでいるのに殺し手から外していない: " + string.Join(" / ", uncovered));
    }

    /// <summary>
    /// <b>0 件は緑ではない。</b> 走査が何も見つけられなくなったら、上の検査は常に緑を返す。
    /// <b>除外表を空にしたら、いま外しているものがそのまま挙がる</b>ことで、走査が生きていると言える。
    /// </summary>
    [Fact]
    public void 字面を読むテストの走査は実際に見つけている()
    {
        var found = SchemaKnockout.TestsReadingSchemaTextOutsideTheTable(
            Path.Combine(TestDatabase.RepositoryDirectory, "BusinessApp", "BusinessApp.Schema.Tests"),
            new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Contains("SchemaShapeTests.計上済み仕訳を守るトリガが揃っている", found);
        Assert.Contains("MasterCodeGuardTests.トリガ12本は同じ条件を持つ", found);
        Assert.Contains(
            "JournalDescriptionGuardTests.トリガが空とみなす字は_char_IsWhiteSpace_と過不足なく一致する",
            found);
    }

    [Fact]
    public void 掃引のフィルタは除外したクラスを全部外す()
    {
        var filter = SchemaKnockout.TestFilter();

        foreach (var name in SchemaKnockout.TestsThatReadSchemaText.Keys)
        {
            Assert.Contains($"FullyQualifiedName!~{name}", filter, StringComparison.Ordinal);
        }
    }

    /// <summary>種類ごとの点の数。</summary>
    private static Dictionary<KnockoutKind, int> Census(IEnumerable<string> ddlTexts)
    {
        var census = new Dictionary<KnockoutKind, int>();
        foreach (var point in ddlTexts.SelectMany(SchemaKnockout.TableConstraintsIn))
        {
            census[point.Kind] = census.GetValueOrDefault(point.Kind) + 1;
        }

        return census;
    }

    private static int CountOf(string text, string keyword)
    {
        var count = 0;
        for (var at = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = text.IndexOf(keyword, at + keyword.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static string SchemaOf(Microsoft.Data.Sqlite.SqliteConnection db)
        => string.Join(
            "\n",
            TestDatabase.Query(db, "SELECT type || ' ' || name || ' ' || COALESCE(sql, '') FROM sqlite_master ORDER BY type, name"));
}
