namespace BusinessApp.Partners.Server.Tests.Fixtures;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 本物の DDL と初期データを載せた SQLite の上に、取引先部品のサーバ側を組み立てて渡す。
/// </summary>
/// <remarks>
/// <para><b>会計側のテスト土台（<c>AccountingServer</c>）を使わない。</b>
/// 部品を分けた（ADR-0025）以上、取引先の検査が会計コアのテストに依存していると、
/// 「取引先アプリと取引先ライブラリだけ用意すれば使える」が検査の側で成立しない。
/// 依存の逆流はコードだけでなくテストでも作らない（ADR-0024 §4）。</para>
/// <para><b>DB は分けない</b>（ADR-0025 §5）ので、載せるのは会計と同じ 1 つの DDL である。
/// 取引先だけを別の DB に切り出すことはしていない。</para>
/// </remarks>
internal sealed class PartnerServer : IDisposable
{
    private readonly SqliteConnection connection;

    public PartnerServer()
    {
        connection = TestDatabase.CreateWithSeed();
        Accessor = new SqliteDbAccessor(connection);

        Registrations = new PartnerRegistrationStore(Accessor, SqliteDbAccessor.DataSourceName);
        Partners = new PartnerStore(Accessor, SqliteDbAccessor.DataSourceName);
    }

    /// <summary>
    /// DB への口。<b>本番と同じ組み立て</b>（<see cref="PartnerSubmitPipeline.Create"/>）を
    /// 通すテストが使う。手で部品を繋ぐと、本番の配線とずれても誰も気づけない。
    /// </summary>
    public SqliteDbAccessor Accessor { get; }

    /// <summary>取引先の名称と登録を読む口。</summary>
    public PartnerRegistrationStore Registrations { get; }

    /// <summary>取引先の素性（種別・法人番号）を読む口。</summary>
    public PartnerStore Partners { get; }

    public void Execute(string sql) => TestDatabase.Execute(connection, sql);

    public T Scalar<T>(string sql) => TestDatabase.ScalarOf<T>(connection, sql);

    /// <summary>
    /// 日付の列に、<b>CLB と同じ形</b>で書く。
    /// </summary>
    /// <remarks>
    /// CLB は日付の列に <c>"2023-10-01 00:00:00"</c> と<b>時刻付き</b>で書く（2026-08-26 実測）。
    /// テストが素の <c>"2023-10-01"</c> を入れると、
    /// <b>文字列で比べている検査が本番では外れるのにテストでは通る</b>（qa/03 L-12）。
    /// </remarks>
    public static string DateLiteral(string date) => $"'{date} 00:00:00'";

    public void Dispose() => connection.Dispose();
}
