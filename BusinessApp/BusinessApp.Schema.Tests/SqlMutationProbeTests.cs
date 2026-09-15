namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

/// <summary>
/// <b>行セットの差分で殺す計器そのものを検体で固定する</b>（ADR-0058）。
/// </summary>
/// <remarks>
/// <para><b>この計器の狂いは、どちらの向きにも出る。</b>
/// 届かなければ<b>全点が「見えない」</b>になって上限を割り（安全側だが計器は死んでいる）、
/// 比べ方が壊れれば<b>全点が「見分けた」</b>になって<b>生き残り 0 という最良の報告</b>が返る。
/// <b>後者が黙って通らないように、両側を撃つ。</b></para>
/// <para><b>判定は入力を受け取って結果を返す形に切り出してある</b>
/// （<see cref="SqlMutationProbe.Read"/> と <see cref="SqlMutationProbe.Render"/>）。
/// 配線だけは<b>本物の経路</b>——<see cref="SqlMutationProbe.ExecuteReader"/> を通して確かめる。</para>
/// </remarks>
[Collection(QuerySqlCollection.Name)]
public class SqlMutationProbeTests
{
    /// <summary>
    /// 配線の検体が使う架空のモジュール名。<b>実在のクエリモジュールを使わない。</b>
    /// </summary>
    /// <remarks>
    /// 本物の SQL を使うと、<b>SQL を直した日にこの検体まで直すことになる</b>。
    /// 計器が見るのは<c>コマンドに載っている字面</c>だけなので、検体は小さくてよい。
    /// </remarks>
    private const string Module = "Probe";

    /// <summary>1 行 1 列の検体。<b>値の型と列の名前を見る検体が共有する。</b></summary>
    private const string OneRow = "SELECT 1 AS a WHERE 1 >= 1";

    /// <summary><see cref="OneRow"/> の値を NULL にした検体。</summary>
    private const string NullRow = "SELECT NULL AS a WHERE 1 >= 1";

    /// <summary>
    /// 報告の書き出し先。<b>毎回同じ名前を使い、上書きする</b>——
    /// 名前を変えると、流すたびに一時ファイルが溜まる。
    /// </summary>
    private static readonly string Report =
        Path.Combine(Path.GetTempPath(), "businessapp-sql-probe-selftest.tsv");

    // --- 変異点を読む ---

    [Fact]
    public void 変異点を読む()
    {
        var plan = SqlMutationProbe.Read(
            "0\tboundary-ge\t12\t>=\t>\tBook:40:2:>:>=\n"
            + "3\tdesc\t20\tDESC\tASC\tBook:80:4:ASC:DESC\n");

        Assert.Equal("Book", plan.Module);
        Assert.Equal([0, 3], plan.Points.Select(point => point.Index));
        Assert.Equal("Book:40:2:>:>=", plan.Points[0].Spec);
    }

    /// <summary>
    /// <b>形が壊れていたら黙って読み飛ばさない。</b>
    /// </summary>
    /// <remarks>
    /// 読み飛ばした点は<b>誰にも当てられないまま「見えない」に数えられる</b>。
    /// 増える向きなので上限では気づけるが、<b>理由が分からなくなる</b>。
    /// </remarks>
    [Theory]
    [InlineData("0\tboundary-ge\t12\t>=\t>")]                    // 欄が足りない
    [InlineData("0\tboundary-ge\t12\t>=\t>\tBook:40:2:>:>=\tx")] // 欄が多い
    [InlineData("あ\tboundary-ge\t12\t>=\t>\tBook:40:2:>:>=")]   // 通番が数でない
    [InlineData("0\ta\t1\t>=\t>\tBook:40:2:>:>=\n0\tb\t2\t<=\t<\tBook:50:2:<:<=")]  // 通番の重複
    [InlineData("0\tboundary-ge\t12\t>=\t>\t:40:2:>:>=")]        // モジュール名が空
    [InlineData("0\tboundary-ge\t12\t>=\t>\tBook")]              // モジュール名だけ
    [InlineData("0\ta\t1\t>=\t>\tBook:40:2:>:>=\n1\tb\t2\t<=\t<\tLedger:50:2:<:<=")]  // 2 つのモジュール
    [InlineData("")]                                             // 空
    [InlineData("\n\n")]                                         // 空行だけ
    public void 形が壊れていたら投げる(string spec)
        => Assert.Throws<ArgumentException>(() => SqlMutationProbe.Read(spec));

    /// <summary><b>行末の CR は落とす。</b> 環境変数が CRLF で渡ってくる経路がある。</summary>
    [Fact]
    public void 行末のCRは落とす()
    {
        var plan = SqlMutationProbe.Read("0\tboundary-ge\t12\t>=\t>\tBook:40:2:>:>=\r\n");

        Assert.Equal("Book:40:2:>:>=", Assert.Single(plan.Points).Spec);
    }

    // --- 報告の姿 ---

    /// <summary>
    /// <b>判定は 3 つに書き分ける。</b> 壊れた点を「見分けた」に混ぜると、
    /// <b>SQL として成り立たないだけの点が、テストの強さとして数えられる</b>。
    /// </summary>
    [Fact]
    public void 報告は見分けた点と見えない点と壊れた点を書き分ける()
    {
        var plan = SqlMutationProbe.Read(
            "0\ta\t1\t>=\t>\tBook:10:2:>:>=\n"
            + "1\tb\t2\t<=\t<\tBook:20:2:<:<=\n"
            + "2\tc\t3\tAND\tOR\tBook:30:3:OR:AND\n");

        Assert.Equal(
            "module\tBook\nobservations\t7\npoints\t3\n0\tkilled\n1\tsurvived\n2\tbroken\n",
            SqlMutationProbe.Render(plan, 7, new HashSet<int> { 0 }, new HashSet<int> { 2 }));
    }

    // --- 配線（本物の経路を通す）---

    /// <summary>
    /// <b>環境変数を立てると、流した入力の分だけ報告が書かれる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>ここが配線の唯一の突き合わせである</b>——
    /// <see cref="SqlMutationProbe.Read"/> と <see cref="SqlMutationProbe.Render"/> の検体は
    /// 純関数を固定するだけで、<b>掃引が立てた環境変数が行動テストの流す SQL まで届くか</b>は
    /// 言っていない。</para>
    /// <para><b>3 つの判定を 1 度に撃つ。</b> 条件を狭める変異（行が減る）・
    /// 結果の変わらない変異・SQL として成り立たない変異を 1 本の SQL に並べてある。</para>
    /// </remarks>
    [Fact]
    public void 環境変数を立てると流した入力の分だけ報告が書かれる()
    {
        const string Sql = "SELECT 1 AS a WHERE 1 >= 1 AND 2 >= 1";

        var narrowed = Sql.IndexOf(">=", StringComparison.Ordinal);
        var unchanged = Sql.LastIndexOf(">=", StringComparison.Ordinal);
        var broken = Sql.IndexOf("SELECT", StringComparison.Ordinal);

        var report = Run(
            Sql,
            $"0\tboundary-ge\t1\t>=\t>\t{Module}:{narrowed}:2:>:>=\n"
            + $"1\tboundary-ge\t1\t>=\t>\t{Module}:{unchanged}:2:>:>=\n"
            + $"2\tbroken\t1\tSELECT\t)\t{Module}:{broken}:6:):SELECT\n");

        // 1 >= 1 を 1 > 1 にすると行が消える → 見分けた。
        // 2 >= 1 を 2 > 1 にしても真のまま → 見えない。
        // SELECT を ) にすると SQL として成り立たない → 壊れた。
        Assert.Equal(
            $"module\t{Module}\nobservations\t1\npoints\t3\n0\tkilled\n1\tsurvived\n2\tbroken\n",
            report);
    }

    /// <summary>
    /// <b>立てていなければ何も起きない。</b> 通常のテストの経路に分岐を増やさない。
    /// </summary>
    [Fact]
    public void 環境変数が立っていなければ報告を書かない()
    {
        File.WriteAllText(Report, "前の回の報告");

        using var db = TestDatabase.Create();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT 1 AS a";
        using var reader = SqlMutationProbe.ExecuteReader(command, Module);

        Assert.True(reader.Read());
        Assert.Equal("前の回の報告", File.ReadAllText(Report));
    }

    /// <summary>
    /// <b>名指されたモジュール以外は素通しする。</b>
    /// </summary>
    [Fact]
    public void 別のモジュールには当てない()
    {
        const string Sql = "SELECT 1 AS a WHERE 1 >= 1";
        var start = Sql.IndexOf(">=", StringComparison.Ordinal);
        var spec = $"0\tboundary-ge\t1\t>=\t>\t{Module}:{start}:2:>:>=";

        Assert.Equal(
            $"module\t{Module}\nobservations\t0\npoints\t1\n0\tsurvived\n",
            Run(Sql, spec, moduleName: "Ledger"));

        // **対照。** 「入力 0 通り・見えない 1」は、名前で弾いたとも計器が動いていないとも読める。
        // **同じ変異点で名前を合わせた回が見分けられること**を並べて、初めて対照になる。
        Assert.Equal(
            $"module\t{Module}\nobservations\t1\npoints\t1\n0\tkilled\n",
            Run(Sql, spec));
    }

    /// <summary>
    /// <b>2 本目の入力で初めて見分けられる点が、ちゃんと見分けられる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>この計器の存在理由そのものである。</b> 1 通りの入力では区別が付かない変異が、
    /// 別の引数を流した途端に区別できるようになる——<b>だから「当てた入力の数」に下限を置いている</b>
    /// （`sql_sweep.ps1` の `Observations`）。</para>
    /// <para><b>入力を 1 通りしか流さない検体だけだと、この枝は 1 度も撃たれない。</b>
    /// 「一度見分けた点はもう当てない」枝（速くするための枝）も同じである。</para>
    /// </remarks>
    [Fact]
    public void 二本目の入力で初めて見分けられる点も見分ける()
    {
        // `@p >= 1` を `@p > 1` にする。@p が 2 なら結果は変わらず、@p が 1 なら行が消える。
        const string Sql = "SELECT 1 AS a WHERE @p >= 1";

        // 1 通りだけ（@p = 2）では見分けられない。
        Assert.Equal(
            $"module\t{Module}\nobservations\t1\npoints\t1\n0\tsurvived\n",
            Run(Sql, Spec(Sql, ">=", ">"), Module, [("@p", 2L)]));

        // 2 通り目（@p = 1）で見分けられる。**観測の数も 2 になる。**
        // **通番を変えて別のセッションにする**——同じ変異点の字面なら観測は積み上がる（Spec の注記）。
        Assert.Equal(
            $"module\t{Module}\nobservations\t2\npoints\t1\n1\tkilled\n",
            Run(Sql, Spec(Sql, ">=", ">", index: 1), Module, [("@p", 2L)], [("@p", 1L)]));
    }

    /// <summary>
    /// <b>NULL と空文字を混ぜない。</b>
    /// </summary>
    /// <remarks>
    /// どちらも空文字に畳むと、<c>COALESCE</c> の段の取り違え（qa/03 の L-19）が
    /// <b>「変わらなかった」に落ちる</b>（ADR-0058 決定 4）。
    /// </remarks>
    [Fact]
    public void NULLと空文字は違う行セットになる()
        => Assert.Equal(
            $"module\t{Module}\nobservations\t1\npoints\t1\n0\tkilled\n",
            Run(NullRow, Spec(NullRow, "SELECT NULL", "SELECT ''"), Module));

    /// <summary>
    /// <b>値の型も混ぜない。</b> 整数の <c>1</c> と文字列の <c>'1'</c> を同じ字に畳むと、
    /// <b>同じ字面になる別の列へ取り違える変異が見分けられなくなる</b>（ADR-0058 決定 4）。
    /// </summary>
    [Fact]
    public void 整数と文字列は違う行セットになる()
        => Assert.Equal(
            $"module\t{Module}\nobservations\t1\npoints\t1\n0\tkilled\n",
            Run(OneRow, Spec(OneRow, "SELECT 1", "SELECT '1'"), Module));

    /// <summary>
    /// <b>列の名前も行セットに入れる。</b> 別名が変われば行動テストは <c>GetOrdinal</c> で落ちて
    /// A 案が「殺した」と数えるので、<b>こちらも見分けたことにしないと部分集合の関係が崩れる</b>。
    /// </summary>
    [Fact]
    public void 列の名前が変われば違う行セットになる()
        => Assert.Equal(
            $"module\t{Module}\nobservations\t1\npoints\t1\n0\tkilled\n",
            Run(OneRow, Spec(OneRow, "AS a", "AS b"), Module));

    // --- 計器そのものの壊れ方 ---

    /// <summary>
    /// <b>当てても 1 文字も変わらない点があれば投げる。</b>
    /// </summary>
    /// <remarks>
    /// 放っておくと<b>必ず「見えない」に数えられ、穴の報告に化ける</b>——
    /// 置換を骨抜きにした日に、計器は全点を穴として報告して止まる。
    /// </remarks>
    [Fact]
    public void 当てても変わらない点があれば投げる()
    {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => Run("SELECT 1 AS a", $"0\tnop\t1\tSELECT\tSELECT\t{Module}:0:6:SELECT:SELECT"));

        Assert.Contains("1 文字も変えていない", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>同じ SQL を 2 度流して行セットが違えば投げる。</b>
    /// </summary>
    /// <remarks>
    /// <b>並びが決まっていない SQL では、変異のせいで変わったと言えない</b>——
    /// 見分けられた点が増える向きに狂うので、<b>上限だけの関門では気づけない</b>。
    /// </remarks>
    [Fact]
    public void 同じSQLを二度流して行が違えば投げる()
    {
        const string Sql = "SELECT random() AS a WHERE 1 >= 1";
        var start = Sql.IndexOf(">=", StringComparison.Ordinal);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => Run(Sql, $"0\tboundary-ge\t1\t>=\t>\t{Module}:{start}:2:>:>="));

        Assert.Contains("2 度流して行セットが違う", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>指した位置に思っていた字が無ければ投げる</b>（<see cref="TestDatabase.Mutate"/> の守り）。
    /// </summary>
    [Fact]
    public void 指した位置に思っていた字が無ければ投げる()
        => Assert.Throws<ArgumentException>(
            () => Run("SELECT 1 AS a WHERE 1 >= 1", $"0\tboundary-ge\t1\t<=\t<\t{Module}:21:2:<:<="));

    /// <summary>
    /// <b>A 案と B 案は同時に回せない。</b>
    /// </summary>
    /// <remarks>
    /// A 案は<b>読み込んだ SQL そのもの</b>を壊すので、
    /// <b>B 案の「原本」が既に壊れたものになる</b>——
    /// 差が出ないほうを「見えない」と報告して、穴の一覧が入れ替わる。
    /// </remarks>
    [Fact]
    public void AB二つの注入が同時に立っていれば投げる()
    {
        try
        {
            Environment.SetEnvironmentVariable(TestDatabase.SqlMutationVariable, "Probe:0:6:X:SELECT");

            var thrown = Assert.Throws<InvalidOperationException>(
                () => Run("SELECT 1 AS a", $"0\tnop\t1\tSELECT\tX\t{Module}:0:6:X:SELECT"));

            Assert.Contains("同時に回せない", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TestDatabase.SqlMutationVariable, null);
        }
    }

    /// <summary>
    /// <b>報告の行き先が無ければ投げる。</b> 書けないまま観測すると、
    /// <b>駆動役は前の回の報告をそのまま読む</b>。
    /// </summary>
    [Fact]
    public void 報告の行き先が無ければ投げる()
    {
        try
        {
            Environment.SetEnvironmentVariable(SqlMutationProbe.SpecVariable, $"0\ta\t1\tA\tB\t{Module}:0:6:X:SELECT");
            Environment.SetEnvironmentVariable(SqlMutationProbe.ReportVariable, null);

            using var db = TestDatabase.Create();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT 1 AS a";

            var thrown = Assert.Throws<InvalidOperationException>(
                () => SqlMutationProbe.ExecuteReader(command, Module));

            Assert.Contains(SqlMutationProbe.ReportVariable, thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SqlMutationProbe.SpecVariable, null);
        }
    }

    // --- 番人 ---

    /// <summary>
    /// <b>行動テストは全部この経路を通っている。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>通っていないクラスがあると、その入力は 1 度も当てられない。</b>
    /// 狂いは「見えない点が増える」向き（安全側）に出るが、
    /// <b>上限を割った理由が「配線が抜けた」なのか「検体が痩せた」なのかが分からなくなる</b>。</para>
    /// <para><b>素の <c>command.ExecuteReader()</c> が残っていないことも見る。</b>
    /// 片方だけ直して両方の経路が並んでいると、<b>当てられる入力と当てられない入力が混ざる</b>。</para>
    /// </remarks>
    [Fact]
    public void 行動テストは全部この経路を通っている()
    {
        foreach (var (name, source) in BehaviorTestSources())
        {
            var routed = Occurrences(source, "SqlMutationProbe.ExecuteReader(command, Module)");

            Assert.True(
                routed >= 1,
                $"{name} が SqlMutationProbe.ExecuteReader を通っていない。"
                + "通らない入力は、行セットの差分で殺す掃引（ADR-0058）に 1 度も当たらない。");

            // **数で挟む。** 「素の `command.ExecuteReader()` が無い」だけでは、
            // **変数名を 1 つ変えるだけで 2 本目の経路が作れる**（`cmd.ExecuteReader()`）。
            // **リーダを開く回数と計器を通る回数が一致していること**なら、名前に依らない。
            Assert.Equal(routed, Occurrences(source, "ExecuteReader("));
        }
    }

    /// <summary>
    /// <b>行動テストは SQL の字面を読まない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>字面を読んで突き合わせる表明が 1 本でも入ると、そのモジュールの全点が「殺した」になる</b>
    /// ——どの変異も字面を変えるからである。<b>A 案の掃引はそれを「見張られている」と数え、
    /// 生き残り 0 という最良の報告を返す</b>（制約ノックアウトが
    /// <c>SchemaKnockout.TestsThatReadSchemaText</c> で外しているのと同じ問題）。</para>
    /// <para><b>除外表を作らずに、字面を読むこと自体を禁じる。</b>
    /// 除外表は<b>入れた人が足さなければ効かない</b>（ADR-0012 §4 の「許容リストは必ず腐る」）。
    /// <b>読む必要が出たら、そのテストを <c>*QueryTests</c> の外へ出す</b>
    /// ——<c>QueryModuleTests</c> が宣言と SQL の整合を見る場所として既にある。</para>
    /// <para><b>数え方は「SQL を手に入れる経路が 1 本だけ」</b>である。
    /// <c>TestDatabase.QuerySql</c> は <c>Run</c> の中の 1 度きりで、
    /// サイドカーの名前（<c>.Query.sql</c>）はどこにも出ない。</para>
    /// </remarks>
    [Fact]
    public void 行動テストはSQLの字面を読まない()
    {
        foreach (var (name, source) in BehaviorTestSources())
        {
            Assert.True(
                Occurrences(source, "TestDatabase.QuerySql") == 1,
                $"{name} が TestDatabase.QuerySql を 2 度以上呼んでいる。"
                + "SQL の字面を読む表明が入ると、そのモジュールの全点が「殺した」に化ける。");

            Assert.False(
                source.Contains(".Query.sql", StringComparison.Ordinal),
                $"{name} がサイドカーの名前を直に書いている。SQL を手に入れる経路は Run の 1 本だけにする。");

            // **`CommandText` は SQL の第 2 の取っ手である。** `Run` の中で一度読んだ SQL は
            // コマンドに載っているので、**`Assert.Contains("ORDER BY", command.CommandText)` を 1 行足すだけで
            // 上の 2 つを素通りする**。**代入の 1 度きりに限る。**
            Assert.True(
                Occurrences(source, "CommandText") == 1,
                $"{name} が CommandText を 2 度以上触っている。"
                + "字面を読む表明が入ると、そのモジュールの全点が「殺した」に化ける。");
        }
    }

    /// <summary>
    /// <paramref name="sql"/> の中の <paramref name="from"/> を <paramref name="to"/> に
    /// 置き換える変異点 1 つ。<b>演算子表に無い形でよい</b>——計器が見るのは字面の置き換えだけである。
    /// </summary>
    /// <param name="index">
    /// 通番。<b>セッションは変異点の字面で同じものと見なされる</b>ので、
    /// <b>1 つのテストで 2 度流したいときは通番を変えて別のセッションにする</b>
    /// （掃引は 1 モジュールに 1 つの変異点表を渡すので、本番ではこの使い分けが要らない）。
    /// </param>
    private static string Spec(string sql, string from, string to, int index = 0)
    {
        var start = sql.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"検体に '{from}' が無い。");

        return $"{index}\tspecimen\t1\t{from}\t{to}\t{Module}:{start}:{from.Length}:{to}:{from}";
    }

    /// <summary>
    /// <b>数え方そのものを検体で固定する。</b> 番人 2 本の判定がこれに乗っているので、
    /// 数え違い（0 件・重なる出現）を入れても誰も鳴らない形にしない。
    /// </summary>
    [Theory]
    [InlineData("", "ab", 0)]
    [InlineData("xyz", "ab", 0)]
    [InlineData("ab", "ab", 1)]
    [InlineData("abab", "ab", 2)]
    [InlineData("aaa", "aa", 1)]          // 重なる出現は数えない（読み進める）
    [InlineData("a.b(a.b(", "a.b(", 2)]
    public void 出現の数え方は検体で固定されている(string source, string text, int expected)
        => Assert.Equal(expected, Occurrences(source, text));

    private static int Occurrences(string source, string text)
    {
        var count = 0;
        for (var at = source.IndexOf(text, StringComparison.Ordinal);
             at >= 0;
             at = source.IndexOf(text, at + text.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// 行動テストのソース（ファイル名と中身）。<b>クエリモジュールの数と突き合わせてから返す。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>下限をベタ書きしない。</b> 「4 本以上あればよい」にすると、
    /// <b>5 本目のモジュールが増えた日に、その行動テストは 1 度も検査されずに緑になる</b>
    /// ——下限はクエリモジュールの実数から取る。</para>
    /// <para><b>下位フォルダも見る。</b> <c>Books/</c> のようなフォルダに置かれた日に
    /// 走査から静かに落ちる形を作らない。</para>
    /// </remarks>
    private static IReadOnlyList<(string Name, string Source)> BehaviorTestSources()
    {
        var found = Directory
            .EnumerateFiles(
                Path.Combine(TestDatabase.RepositoryDirectory, "BusinessApp", "BusinessApp.Schema.Tests"),
                "*QueryTests.cs",
                SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)))
            .ToList();

        var modules = Directory
            .EnumerateFiles(TestDatabase.ModulesDirectory, "*.Query.sql", SearchOption.AllDirectories)
            .Count();

        Assert.True(
            found.Count >= modules,
            $"行動テストのソースが {found.Count} 本しか見つからない（クエリモジュールは {modules} 本）。"
            + "探し先か名前の規約を確かめること。");

        return found;
    }

    // --- 実行の土台 ---

    /// <summary>
    /// 本物の経路（<see cref="SqlMutationProbe.ExecuteReader"/>）で流し、書かれた報告を返す。
    /// </summary>
    /// <param name="sql">流す SQL。<b>入力を 2 通り以上渡すと、同じ変異点に順に当たる。</b></param>
    /// <param name="spec">変異点。</param>
    /// <param name="moduleName">名指すモジュール。既定は <see cref="Module"/>。</param>
    /// <param name="parameters">
    /// 引数の組。<b>1 つの組が 1 通りの入力</b>で、空なら引数なしで 1 度流す。
    /// </param>
    private static string Run(
        string sql,
        string spec,
        string moduleName = Module,
        params (string Name, object Value)[][] parameters)
    {
        try
        {
            Environment.SetEnvironmentVariable(SqlMutationProbe.SpecVariable, spec);
            Environment.SetEnvironmentVariable(SqlMutationProbe.ReportVariable, Report);

            using var db = TestDatabase.Create();
            var inputs = parameters.Length == 0 ? [[]] : parameters;

            foreach (var input in inputs)
            {
                using var command = db.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in input)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                using (SqlMutationProbe.ExecuteReader(command, moduleName))
                {
                    // 行は読まない。**計器が見るのは、流した入力と変異の結果だけ**である。
                }
            }

            return File.ReadAllText(Report);
        }
        finally
        {
            // **必ず消す。** 残すと、同じプロセスの後続のテストまで観測してしまう。
            Environment.SetEnvironmentVariable(SqlMutationProbe.SpecVariable, null);
            Environment.SetEnvironmentVariable(SqlMutationProbe.ReportVariable, null);
        }
    }
}
