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
    public static SqliteConnection Create() => CreateFromFiles(DdlFiles());

    /// <summary>
    /// 指定した SQL ファイルを順に適用した接続を返す。マイグレーションの同値検査
    /// （baseline ＋ migrations の再生。ADR-0020）が使う。
    /// </summary>
    public static SqliteConnection CreateFromFiles(IEnumerable<string> sqlFiles)
    {
        var name = $"testdb-{Guid.NewGuid():N}";
        var connection = Connect(name);

        foreach (var file in sqlFiles)
        {
            Execute(connection, File.ReadAllText(file));
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
    /// <b>外せるトリガは 3 本とも <c>journal_entries</c> の同じ <c>BEFORE UPDATE</c>（下書き → 計上）に張ってある</b>ので、
    /// 片方を外して貼り直すと、その接続では<b>その 1 本が最後に作られたトリガになる</b>——
    /// 複数に当たる検体（摘要が空で、かつ補助科目や取引先の規則も破っている伝票）を作ると、
    /// <b>どの断りが返るかが本番と入れ替わりうる</b>。
    /// いまの検体はどれか 1 つしか破っていないので、この差は出ていない。</para>
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
        // **規則より前に計上された行が稼働 DB に 10 行あり**（売掛金 1・買掛金 7・外注費 2。
        // 2026-09-08 実測。数え方は qa/04）、**それらを取り消せることが免除の根拠**なので、
        // 検体が要る（docs/10 §6-2）。
        "trg_journal_entries_partner_presence_when_posted",
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
