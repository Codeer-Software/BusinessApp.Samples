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
/// <para><b>トランザクションは本物を張る。</b> 会計コアの安全性は「関門が例外を投げれば
/// 保存ごと巻き戻る」ことに全面的に依存している（ADR-0004）。オートコミットで走らせると、
/// その最も大事な性質だけが検査されないまま緑になる。</para>
/// <para>型定義の取得と値変換は会計コアのサーバ側部品が使っていない。使い始めたら、
/// そのとき実装する。今から作り込むと「呼ばれないコード」を検証なしに抱えることになる。</para>
/// <para><b>Query と Execute でパラメータ辞書の型が違う</b>のは本物の
/// <see cref="IDbAccessor"/> の仕様であり、ここでも同じにしてある（qa/01 C-12）。</para>
/// </remarks>
public sealed class SqliteDbAccessor(SqliteConnection connection) : IDbAccessor
{
    /// <summary>データソース名は 1 つしかないので、渡された名前は照合だけして使わない。</summary>
    public const string DataSourceName = "TestSqlite";

    private SqliteTransaction? transaction;

    /// <summary>
    /// <b>SQL を流す直前に呼ばれる。例外を返すとその 1 文が失敗する。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>途中で落ちたときに何が残るかを検査するための口である。</b>
    /// 訂正は「取消を計上する」「再計上の下書きを作る」の 2 つで 1 操作であり、
    /// <b>途中で失敗したときに取消だけが残らない</b>ことが機能の要件そのものである
    /// （ADR-0015・ADR-0016 が A 案を選んだ決め手）。仕掛けが無いと、
    /// <b>この最も大事な性質だけが検査されないまま緑になる</b>（qa/02 R4-05）。</para>
    /// <para>渡すのは実行しようとしている SQL。テスト側が何回目かを数えるなり
    /// 文面で分岐するなり決める。</para>
    /// <para><b>問い合わせにも効かせる。</b> 下書きの挿入は <c>returning id</c> のために
    /// <c>QueryAsync</c> を通るので、書き込みだけに掛けると<b>いちばん落としたい 1 文に届かない</b>。</para>
    /// </remarks>
    public Func<string, Exception?>? FailBeforeStatement { get; set; }

    public async Task<List<IDictionary<string, object>>> QueryAsync(
        string dataSourceName, string sql, Dictionary<string, ParamAndRawDbTypeName> parameters)
    {
        Fail(sql);

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
        Fail(sql);

        using var command = CreateCommand(dataSourceName, sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>仕掛けが例外を返したらここで投げる。</summary>
    private void Fail(string sql)
    {
        if (FailBeforeStatement?.Invoke(sql) is Exception failure)
        {
            throw failure;
        }
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
        command.Transaction = transaction;
        return command;
    }

    // --- トランザクション ---

    public void StartTransaction()
    {
        if (transaction is not null)
        {
            throw new InvalidOperationException("トランザクションが二重に始まっている。");
        }

        transaction = connection.BeginTransaction();
    }

    public async Task CommitAsync()
    {
        await Current.CommitAsync();
        Clear();
    }

    public async Task RollbackAsync()
    {
        await Current.RollbackAsync();
        Clear();
    }

    private SqliteTransaction Current
        => transaction ?? throw new InvalidOperationException("トランザクションが始まっていない。");

    private void Clear()
    {
        transaction!.Dispose();
        transaction = null;
    }

    // --- ここから下は会計コアのサーバ側部品が使っていない ---

    public DataSource? GetDataSource(string dataSourceName) => throw NotUsed();

    public System.Data.Common.DbConnection GetConnection(string dataSourceName) => connection;

    public System.Data.IDbTransaction GetTransaction(string dataSourceName) => Current;

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
