namespace BusinessApp.TestSupport;

using System.Globalization;

using Microsoft.Data.Sqlite;

/// <summary>
/// <c>Designer/ddl/</c> の DDL を使い捨ての SQLite に適用して返す。
/// </summary>
/// <remarks>
/// <b>テストプロジェクトの外に置いてある。</b> スキーマの検査（<c>Schema.Tests</c>）と
/// サーバ側部品の検査（<c>AccountingCore.Server.Tests</c>）の両方が使うので、
/// どちらかの中に置くと、もう一方がテストプロジェクトを参照する歪んだ形になる。
/// </remarks>
/// <remarks>
/// <para><b>本物の DDL ファイルをそのまま流す。</b> テスト用に書き写したスキーマを使うと、
/// 写し間違いを検出できないうえ、DDL を直したときに両方を直す必要が出て必ず腐る。</para>
/// <para>接続ごとにインメモリ DB を作るので、テストは互いに干渉しない。
/// SQLite の外部キーは<b>接続ごと</b>に有効化する必要があるため、接続文字列で明示する
/// （実際の稼働 DB でも有効になっていることは確認済み）。</para>
/// </remarks>
public static class TestDatabase
{
    /// <summary>DDL を適用済みの、開いた接続を返す。閉じるとデータは消える。</summary>
    /// <remarks>
    /// <b>共有キャッシュの名前つきインメモリ DB を使う。</b> <c>:memory:</c> は接続ごとに
    /// 別の DB になるので、同時実行（2 接続が同じ行を取り合う）を検査できない。
    /// 名前はテストごとに変えて、テスト同士が干渉しないようにする。
    /// </remarks>
    /// <remarks>
    /// <b>制約ノックアウトの掃引中だけ、制約を 1 つ外した DB を返す</b>（ADR-0053 決定 4）。
    /// <see cref="SchemaKnockout.EnvironmentVariable"/> が設定されていなければ何も起きないので、
    /// <b>通常のテストの経路に分岐は増えない</b>。
    /// </remarks>
    public static SqliteConnection Create()
    {
        var knockout = SchemaKnockout.Requested;
        return knockout is null ? CreateFromFiles(DdlFiles()) : SchemaKnockout.CreateWithout(knockout);
    }

    /// <summary>
    /// 指定した SQL ファイルを順に適用した接続を返す。マイグレーションの同値検査
    /// （baseline ＋ migrations の再生。ADR-0020）が使う。
    /// </summary>
    public static SqliteConnection CreateFromFiles(IEnumerable<string> sqlFiles)
        => CreateFromSql(sqlFiles.Select(File.ReadAllText));

    /// <summary>
    /// 指定した SQL 本文を順に適用した接続を返す。<b>制約を 1 つ外した DDL を流すために要る</b>
    /// （ADR-0053。ファイルに書き出さずにそのまま流す）。
    /// </summary>
    public static SqliteConnection CreateFromSql(IEnumerable<string> sqlTexts)
    {
        var name = $"testdb-{Guid.NewGuid():N}";
        var connection = Connect(name);

        foreach (var sql in sqlTexts)
        {
            Execute(connection, sql);
        }

        return connection;
    }

    /// <summary>
    /// トリガを 1 本だけ外して <paramref name="sql"/> を流し、外す前の定義で貼り直す。
    /// </summary>
    /// <remarks>
    /// <para><b>いまの関門なら作れない行を、テストの中で再現するためだけに使う。</b>
    /// 実例: 摘要のない計上済みの伝票（docs/10 §4-2-1）。いまは計上できないが、
    /// <b>規則より前に計上された行が稼働 DB に 2 件あり、計上済みは直せない</b>（I-05）。
    /// 帳簿はその行も探せなければならない（電帳通達 8-13）ので、検体にも同じ行が要る。</para>
    /// <para><b>定義は <c>sqlite_master</c> から読む。</b> テストに書き写すと、
    /// 正典（<c>Designer/ddl/</c>）を直したときに写しだけが古くなり、
    /// <b>守っているつもりで別物を貼り直す</b>ことになる。</para>
    /// <para><b>普通の検体づくりに使わない。</b> 使うたびに「なぜこの行が作れないのか」を
    /// 呼ぶ側のコメントに書くこと。</para>
    /// <para><b>貼り直したトリガは、その表の中で最後に作られた状態になる。</b>
    /// 発火順は SQLite の仕様上 undefined で、実測では後に作ったものから鳴るので、
    /// <b>複数のトリガが同時に当たる検体では、鳴る順が本番と変わりうる。</b>
    /// <b>許可表は 2026-09-23 に 5 本になり、表も契機もばらばらである</b>
    /// （<c>journal_entries</c> の <c>BEFORE UPDATE</c> が 3 本と、制度ルールの 2 表の <c>BEFORE INSERT</c> が各 1 本）。
    /// <b>制度ルールの表ではこの差が効きうる</b>——<c>Designer/ddl/015</c> は
    /// <b>重なりのトリガに門を置いて発火順に寄りかからない形にしてある</b>が、
    /// <b>門が効くのは開始日が読めない値のときだけ</b>で、
    /// <b>それ以外で同時に鳴る条件に入る検体を書けば、貼り直した接続では順が変わりうる</b>。
    /// <b>いまの検体はどれか 1 つしか破っていないので、この差は出ていない。</b></para>
    /// </remarks>
    public static void WithoutTrigger(SqliteConnection connection, string triggerName, string sql)
    {
        if (!RemovableTriggers.Contains(triggerName))
        {
            throw new InvalidOperationException(
                $"{triggerName} は外せない。外してよいトリガは {string.Join(" / ", RemovableTriggers)} だけである。");
        }

        var definition = ScalarOf<string>(
            connection,
            $"SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = '{triggerName}'");

        // **制約ノックアウトが同じトリガを外している回は、外れている状態で進む**（ADR-0053）。
        // ここで投げると「検体が作れなかった赤」になり、掃引はそれを
        // **「テストが見張っている」と数えてしまう**——決定 2 が避けたい形そのものである。
        if (string.IsNullOrEmpty(definition) && SchemaKnockout.Requested == $"trigger:{triggerName}")
        {
            Execute(connection, sql);
            return;
        }

        // 名前を打ち間違えると、外れないまま静かに通ってしまう（=> 検体が作れず別の理由で落ちる）。
        if (string.IsNullOrEmpty(definition))
        {
            throw new InvalidOperationException($"トリガ {triggerName} がこの DB に無い。名前を確かめること。");
        }

        Execute(connection, $"DROP TRIGGER {triggerName}");
        try
        {
            Execute(connection, sql);
        }
        finally
        {
            // **落ちても必ず貼り直す。** 貼り直さないと、その接続の残りのテストが
            // 「トリガの無い DB」で緑になる——外した本人ではなく、後続が静かに嘘をつく。
            Execute(connection, definition);
        }

        if (ScalarOf<long>(
                connection,
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = '{triggerName}'") != 1)
        {
            throw new InvalidOperationException($"トリガ {triggerName} を貼り直せなかった。");
        }
    }

    /// <summary>
    /// <see cref="WithoutTrigger"/> で外してよいトリガ。<b>ここに無い名前は投げる。</b>
    /// </summary>
    /// <remarks>
    /// <b>「検体が作れないときの逃げ道」にしないための許可表である。</b> 名前を自由に取ると、
    /// I-05（計上済みは変更も削除もされない）の守りごと外した検体が書けてしまい、
    /// <b>ADR-0004 が「規約ではなく機械に守らせる」と決めた場所を、テストが規約に戻す。</b>
    /// 足すときは、<b>なぜその行が正規の経路で作れないのか</b>を理由に書くこと。
    /// </remarks>
    private static readonly string[] RemovableTriggers =
    [
        // 摘要のない計上済みは、いまは作れない。**規則より前に計上された行が稼働 DB に 2 件あり、
        // 帳簿はその行も探せなければならない**（電帳通達 8-13。docs/10 §4-2-1）。
        "trg_journal_entries_description_required_when_posted",

        // 補助科目を使わない科目に補助科目が付いた計上済みの明細も、いまは作れない。
        // **規則より前に計上された行が稼働 DB に 1 行あり**（伝票 36。qa/04 の 2026-09-08）、
        // **その伝票を取り消せることがこの規則の免除の根拠**なので、検体が要る（ADR-0038 §3）。
        "trg_journal_entries_sub_account_presence_when_posted",

        // 取引先を要する科目に取引先の無い計上済みの明細も、いまは作れない。
        // **規則より前に計上された行が稼働 DB に実在し**（件数と数え方は qa/04）、
        // **それらを取り消せることが免除の根拠**なので、検体が要る（docs/15 §1-2）。
        "trg_journal_entries_partner_presence_when_posted",

        // 期間の重なる制度ルールの行も、正規の経路では作れない。
        // **取込や直打ちで入りうる**ので、**読み出し側（EffectiveDatedRuleSet）も重なりを拒む**——
        // その守りを撃つ検体が要る（ADR-0069。守りは DB と読み出しの 2 枚である）。
        "trg_transition_purchase_rates_no_overlap_insert",

        // **税率の表でも同じ**。加えて、**一意索引そのものを撃つにはこれを外すしかない**——
        // 同じ区分で同じ日から始まれば必ず重なるので、**トリガが先に断って索引まで届かない**
        // （TaxRateConstraintTests。014 の同名のテストと同じ形）。
        "trg_tax_rates_no_overlap_insert",
    ];

    /// <summary>
    /// 同じインメモリ DB への 2 本目の接続。同時実行の検査に使う。
    /// <b>1 本目を閉じると DB ごと消える</b>ので、こちらを先に閉じること。
    /// </summary>
    public static SqliteConnection Connect(SqliteConnection existing)
        => Connect(new SqliteConnectionStringBuilder(existing.ConnectionString).DataSource);

    private static SqliteConnection Connect(string name)
    {
        var connection = new SqliteConnection(
            $"Data Source={name};Mode=Memory;Cache=Shared;Foreign Keys=True");
        connection.Open();
        return connection;
    }

    /// <summary>DDL に加えて初期データ（<c>Designer/seed/</c>）も適用した接続を返す。</summary>
    public static SqliteConnection CreateWithSeed()
    {
        var connection = Create();
        foreach (var file in SeedFiles())
        {
            Execute(connection, File.ReadAllText(file));
        }

        return connection;
    }

    /// <summary>番号順の DDL ファイル。適用順は外部キーの向きで決まっている。</summary>
    public static IReadOnlyList<string> DdlFiles() => NumberedSqlFiles(DdlDirectory);

    /// <summary>番号順の初期データファイル。</summary>
    public static IReadOnlyList<string> SeedFiles() => NumberedSqlFiles(SeedDirectory);

    /// <summary>
    /// 番号順のマイグレーションファイル（<c>Designer/migrations/</c> 直下のみ。
    /// <c>baseline/</c> は含まない）。
    /// </summary>
    public static IReadOnlyList<string> MigrationFiles() => NumberedSqlFiles(MigrationsDirectory);

    /// <summary>同値検査の起点（<c>ddl/</c> の凍結コピー）。番号順。</summary>
    public static IReadOnlyList<string> BaselineFiles() => NumberedSqlFiles(BaselineDirectory);

    /// <summary>
    /// 開発・デモ専用の初期データ（<c>Designer/seed/dev/</c>）。番号順。
    /// </summary>
    /// <remarks>
    /// <b>実運用には投入しない</b>（ADR-0039）。ここに置いてあるのは、
    /// <b>誰も実行しないファイルを追跡下に置かない</b>ためである——
    /// 列名も役割の値も、流してみるまで誰も確かめていなかった（qa/02 R28-13）。
    /// </remarks>
    public static IReadOnlyList<string> DevSeedFiles()
        => NumberedSqlFiles(Path.Combine(SeedDirectory, "dev"));

    private static IReadOnlyList<string> NumberedSqlFiles(string directory)
        => Directory.GetFiles(directory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    /// <summary>リポジトリのルート。<b>テストのソースそのものを走査する検査</b>が使う。</summary>
    public static string RepositoryDirectory { get; } = RepositoryRoot();

    public static string DdlDirectory { get; } = Path.Combine(RepositoryRoot(), "Designer", "ddl");

    public static string SeedDirectory { get; } = Path.Combine(RepositoryRoot(), "Designer", "seed");

    public static string MigrationsDirectory { get; } = Path.Combine(RepositoryRoot(), "Designer", "migrations");

    public static string BaselineDirectory { get; } = Path.Combine(RepositoryRoot(), "Designer", "migrations", "baseline");

    /// <summary>
    /// CLB のモジュール定義の置き場。<b>帳簿のクエリ（<c>*.Query.sql</c>）を本物のまま検査する</b>ために使う。
    /// </summary>
    /// <remarks>
    /// クエリモジュールの SQL は JSON ではなく別ファイルにあり（<c>_specs/QueryAndSql.md</c>）、
    /// <c>designcheck</c> は中身を実行しない。列名の綴り違いも結合の誤りも、
    /// <b>実機で画面を開くまで分からない</b>。ここで本物の DDL に流して潰す。
    /// </remarks>
    public static string ModulesDirectory { get; } =
        Path.Combine(RepositoryRoot(), "Designer", "Design", "Modules");

    /// <summary>
    /// クエリモジュールの SQL を<b>名前で探す</b>。
    /// </summary>
    /// <remarks>
    /// <b>フォルダを直書きしない。</b> `Modules/` の下の分け方は「どのアプリのものか」で決まり、
    /// 部品が増えるたびに動く（`Designer/Project.md` のフォルダ規約）。
    /// 直書きすると、**フォルダを動かした日にテストが落ちる**——実際に 2026-08-31 の
    /// アプリごとの分割で `Books/` を直書きした 2 本が落ちた。
    /// <b>モジュール名はデザイン全体でフラットな名前空間</b>なので、名前で探せば足りる。
    /// </remarks>
    public static string QuerySqlOf(string moduleName)
    {
        // **2 通りの失敗を区別する。** `SingleOrDefault` は重複のときに英語の
        // `InvalidOperationException` を投げるので、用意したメッセージが出ない。
        var found = Directory
            .EnumerateFiles(ModulesDirectory, $"{moduleName}.Query.sql", SearchOption.AllDirectories)
            .Take(2)
            .ToList();

        return found.Count switch
        {
            1 => found[0],
            0 => throw new FileNotFoundException($"{moduleName}.Query.sql が Modules/ の下に無い"),
            _ => throw new InvalidOperationException(
                $"{moduleName}.Query.sql が Modules/ の下に 2 つ以上ある"),
        };
    }

    /// <summary>
    /// クエリモジュールの SQL を<b>本文で</b>読む。<b>行動テストはこちらを通す。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>読み込みを 1 箇所に集めてある。</b> 各テストが <c>File.ReadAllText</c> を
    /// 自分で書くと、<b>注入が効くテストと効かないテストが混ざり、
    /// 生き残りの数が「殺せなかった」のか「注入が届かなかった」のか分からなくなる</b>
    /// （制約ノックアウトが環境変数 1 本に集めたのと同じ理由。ADR-0053）。</para>
    /// <para><b>SQL ミューテーション（ADR-0056）の注入点である。</b>
    /// <see cref="SqlMutationVariable"/> が立っているときだけ、
    /// <b>その 1 箇所を置き換えた SQL を返す</b>。立っていなければ原本をそのまま返す。</para>
    /// </remarks>
    public static string QuerySql(string moduleName)
    {
        var sql = File.ReadAllText(QuerySqlOf(moduleName));
        var mutation = Environment.GetEnvironmentVariable(SqlMutationVariable);

        // **空白だけの値も「立っていない」と読む。** シェルによっては、消したつもりの変数が
        // 空文字で残る（`SchemaKnockout.Requested` と同じ作法）。
        //
        // **ただし値は切り詰めない。** 最後の欄（原文）は `DISTINCT ` のように
        // **末尾に空白を持つことがあり、切り詰めると照合が必ず外れる**
        // ——実際に `Trim()` を入れた回に、カナリアがそれを捕まえた（qa/02 のラウンド 92）。
        return string.IsNullOrWhiteSpace(mutation) ? sql : Mutate(sql, moduleName, mutation);
    }

    /// <summary>SQL ミューテーションの注入に使う環境変数（ADR-0056）。</summary>
    public const string SqlMutationVariable = "SQL_MUTATION";

    /// <summary>
    /// <c>&lt;モジュール名&gt;:&lt;開始位置&gt;:&lt;長さ&gt;:&lt;置換後&gt;:&lt;原文&gt;</c> を当てる。
    /// </summary>
    /// <remarks>
    /// <para><b>位置で指す。</b> 同じ字面が何度も出るからである——
    /// <c>date(</c> も <c>OR</c> も 1 本の SQL に何十個もあり、字面では 1 つを名指せない。
    /// <b>置換後は空でよい</b>（<c>DISTINCT</c> の削除など）。
    /// <b>置換後に <c>:</c> を入れてはいけない</b>——最後の欄（原文）だけが <c>:</c> を含んでよい。</para>
    /// <para><b>位置だけでは足りない。</b> 数える側（<c>sql_mutate.py</c>）と読む側（ここ）が
    /// <b>同じ文字列を見ている保証は無い</b>——BOM が 1 つ付いただけ、改行が <c>CRLF</c> になっただけで
    /// <b>全部の位置が 1 文字ずれる</b>。ずれた置換は<b>構文として通ることがあり、
    /// 結果が変わって赤くなり、掃引はそれを「殺した」と数える</b>。
    /// だから<b>原文まで運ばせて、そこに本当にその字があるかを確かめる</b>。</para>
    /// <para><b>名指されたモジュール以外は素通しする。</b> 1 回の掃引で流すテストが
    /// 2 つ以上のモジュールに触ることがあり、<b>全部に当てると「どれが殺したか」が分からなくなる</b>。</para>
    /// <para><b>形が壊れていたら黙って素通ししない。</b>
    /// <b>注入が届いていないのに緑を返す</b>のが、この手の道具で最も危ない壊れ方である
    /// ——掃引はそれを「テストが見張っている」と数えてしまう。</para>
    /// <para><b>ファイルを書き換えない。</b> 掃引の途中で止めると<b>壊れた SQL が追跡下に残る</b>——
    /// 環境変数なら、プロセスが終われば何も残らない。</para>
    /// </remarks>
    public static string Mutate(string sql, string moduleName, string? mutation)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(moduleName);
        var parts = (mutation ?? string.Empty).Split(':', 5);

        if (parts.Length != 5
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            throw new ArgumentException(
                $"{SqlMutationVariable} の形が違う: '{mutation}'"
                + "（<モジュール名>:<開始位置>:<長さ>:<置換後>:<原文> である）", nameof(mutation));
        }

        if (!string.Equals(parts[0], moduleName, StringComparison.Ordinal))
        {
            return sql;
        }

        // **足し算で書かない。** `start` が大きいと桁が溢れて負になり、この検査を素通りする。
        if (start > sql.Length - length)
        {
            throw new ArgumentException(
                $"{SqlMutationVariable} が {moduleName} の外を指している: {start}+{length}"
                + $"（全体で {sql.Length} 文字）", nameof(mutation));
        }

        var found = sql.Substring(start, length);
        if (!string.Equals(found, parts[4], StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{SqlMutationVariable} が {moduleName} の別の場所を指している。"
                + $"位置 {start} にあるのは '{found}' で、当てるはずの '{parts[4]}' ではない"
                + "（SQL を直したあとに掃引を流し直していないか、読み方がずれている）。", nameof(mutation));
        }

        return sql[..start] + parts[3] + sql[(start + length)..];
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>1 つの値を読む。</summary>
    /// <remarks>
    /// <para><b>NULL を空文字に化けさせない。</b> <c>Convert.ChangeType(DBNull.Value, typeof(string))</c> は
    /// 例外ではなく <c>""</c> を返す。そのまま返していたので、
    /// <b>「この列は NULL のはず」と書いた表明が、実際には値が入っていても必ず通っていた</b>
    /// （2026-08-26 実測。qa/03 L-11）。</para>
    /// <para>NULL を表せない型（<c>long</c> など）で NULL を読んだら<b>止める</b>。
    /// 既定値（0）を返すと、今度は「0 のはず」という表明が黙って通る。</para>
    /// </remarks>
    public static T ScalarOf<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();

        if (value is null or DBNull)
        {
            return default(T) is null
                ? default!
                : throw new InvalidOperationException(
                    $"値が NULL だが {typeof(T).Name} は NULL を表せない。{typeof(T).Name}? で受けること: {sql}");
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> Query(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();

        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString()!);
        }

        return values;
    }

    /// <summary>
    /// 出力ディレクトリから遡ってリポジトリのルート（<c>BusinessApp.slnx</c> のある場所）を探す。
    /// 絶対パスをコードに書かない（CLAUDE.md §5）。
    /// </summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.slnx").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("リポジトリのルート（*.slnx のある場所）を特定できない。");
    }
}
