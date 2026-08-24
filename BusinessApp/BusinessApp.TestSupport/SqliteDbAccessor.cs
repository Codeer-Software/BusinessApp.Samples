namespace BusinessApp.TestSupport;

using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.SystemSettings;
using Microsoft.Data.Sqlite;

/// <summary>
/// 使い捨ての SQLite に繋がる <see cref="IDbAccessor"/>。
/// </summary>
/// <remarks>
/// <para><b>本物の DDL・本物の SQL を通す。</b> 問い合わせを模造した偽物では、
/// 列名の綴り違いも制約やトリガとの噛み合わせも検出できない。ここで検証したいのは
/// まさにそこなので、SQL は実際に SQLite に流す。</para>
/// <para>CLB の実装が持つ機能（トランザクション・型定義の取得・値変換）は、
/// 会計コアのサーバ側部品が使っていない。使い始めたら、そのとき実装する。
/// 今から作り込むと「呼ばれないコード」を検証なしに抱えることになる。</para>
/// <para><b>Query と Execute でパラメータ辞書の型が違う</b>のは本物の
/// <see cref="IDbAccessor"/> の仕様であり、ここでも同じにしてある（qa/01 C-12）。</para>
/// </remarks>
public sealed class SqliteDbAccessor(SqliteConnection connection) : IDbAccessor
{
    /// <summary>データソース名は 1 つしかないので、渡された名前は照合だけして使わない。</summary>
    public const string DataSourceName = "TestSqlite";

    public async Task<List<IDictionary<string, object>>> QueryAsync(
        string dataSourceName, string sql, Dictionary<string, ParamAndRawDbTypeName> parameters)
    {
        using var command = CreateCommand(dataSourceName, sql);
        foreach (var (name, parameter) in parameters)
        {
            command.Parameters.AddWithValue(name, parameter.Value ?? DBNull.Value);
        }

        using var reader = await command.ExecuteReaderAsync();

        var rows = new List<IDictionary<string, object>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public async Task<int> ExecuteAsync(
        string dataSourceName, string sql, Dictionary<string, object?> parameters)
    {
        using var command = CreateCommand(dataSourceName, sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    private SqliteCommand CreateCommand(string dataSourceName, string sql)
    {
        if (dataSourceName != DataSourceName)
        {
            throw new InvalidOperationException(
                $"知らないデータソース {dataSourceName}（このテストは {DataSourceName} だけを持つ）。");
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    // --- ここから下は会計コアのサーバ側部品が使っていない ---

    public DataSource? GetDataSource(string dataSourceName) => throw NotUsed();

    public System.Data.Common.DbConnection GetConnection(string dataSourceName) => throw NotUsed();

    public System.Data.IDbTransaction GetTransaction(string dataSourceName) => throw NotUsed();

    public void StartTransaction() => throw NotUsed();

    public Task CommitAsync() => throw NotUsed();

    public Task RollbackAsync() => throw NotUsed();

    public DbTableDefinitionCache DbTableDefinitionCache => throw NotUsed();

    public Task<List<DbTableDefinition>?> GetCustomTableDefinitionsAsync(string dataSourceName) => throw NotUsed();

    public Task<DbTableDefinition?> GetCustomTableDefinitionsAsync(string dataSourceName, string tableName) => throw NotUsed();

    public Task<string> InsertAsync(string dataSourceName, string tableName, Dictionary<string, object?> values) => throw NotUsed();

    public object? ConvertFieldValueToDbValue(string dataSourceName, string rawDbTypeName, object? value) => throw NotUsed();

    public object? ConvertDbValueToFieldValue(string dataSourceName, object? value, Type type) => throw NotUsed();

    public Task<DbSqlCommandResult> ExecuteSqlCommandAsync(string dataSourceName, DbSqlCommand command) => throw NotUsed();

    /// <summary>接続の後始末はテスト側（<c>using</c> した接続）が持つ。</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static NotSupportedException NotUsed()
        => new("会計コアのサーバ側部品はこの API を使っていない。使い始めたときに実装する。");
}
