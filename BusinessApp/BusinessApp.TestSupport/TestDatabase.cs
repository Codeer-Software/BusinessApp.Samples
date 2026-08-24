namespace BusinessApp.TestSupport;

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
    public static SqliteConnection Create()
    {
        var name = $"testdb-{Guid.NewGuid():N}";
        var connection = Connect(name);

        foreach (var file in DdlFiles())
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

    private static IReadOnlyList<string> NumberedSqlFiles(string directory)
        => Directory.GetFiles(directory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

    public static string DdlDirectory { get; } = Path.Combine(RepositoryRoot(), "Designer", "ddl");

    public static string SeedDirectory { get; } = Path.Combine(RepositoryRoot(), "Designer", "seed");

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static T ScalarOf<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
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
