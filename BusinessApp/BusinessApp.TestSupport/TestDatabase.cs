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
