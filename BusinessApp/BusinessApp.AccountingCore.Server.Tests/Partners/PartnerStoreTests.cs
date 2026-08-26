namespace BusinessApp.AccountingCore.Server.Tests.Partners;

using BusinessApp.AccountingCore.Partners;
using BusinessApp.AccountingCore.Server.Partners;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

/// <summary>
/// 取引先の素性を読む口（docs/07 §1-2）。
/// </summary>
/// <remarks>
/// 保存の関門は、差分に無い項目をここから補う。<b>読み違えると関門が静かに素通りする。</b>
/// </remarks>
public class PartnerStoreTests
{
    [Fact]
    public async Task 素性をそのまま読み戻せる()
    {
        using var server = new AccountingServer();
        server.Execute("""
            insert into partners (code, name, entity_type, corporate_number)
            values ('P900', '株式会社ベガ商会', 'corporation', '8700110005901')
            """);
        var id = new PartnerId(server.Scalar<long>("select last_insert_rowid()"));

        var profile = await server.Partners.FindProfileAsync(id);

        Assert.Equal(new PartnerProfile(PartnerEntityType.Corporation, "8700110005901"), profile);
    }

    /// <summary>未分類（NULL）を偽の種別で埋めない（docs/07 §1-2）。</summary>
    [Fact]
    public async Task 未分類と未入力は_null_で返る()
    {
        using var server = new AccountingServer();
        server.Execute("insert into partners (code, name) values ('P900', '素性のまだ無い取引先')");
        var id = new PartnerId(server.Scalar<long>("select last_insert_rowid()"));

        var profile = await server.Partners.FindProfileAsync(id);

        Assert.Equal(new PartnerProfile(null, null), profile);
    }

    /// <summary>
    /// 種別の 4 値すべてを読み戻せる。<b>1 値だけ検査すると、対応表が 1 つずれても緑になる。</b>
    /// </summary>
    [Theory]
    [InlineData("corporation", PartnerEntityType.Corporation)]
    [InlineData("sole_proprietor", PartnerEntityType.SoleProprietor)]
    [InlineData("unincorporated_association", PartnerEntityType.UnincorporatedAssociation)]
    [InlineData("other", PartnerEntityType.Other)]
    public async Task 種別は_snake_case_から読み戻せる(string stored, PartnerEntityType expected)
    {
        using var server = new AccountingServer();
        server.Execute($"insert into partners (code, name, entity_type) values ('P900', 'X', '{stored}')");
        var id = new PartnerId(server.Scalar<long>("select last_insert_rowid()"));

        var profile = await server.Partners.FindProfileAsync(id);

        Assert.Equal(expected, profile?.EntityType);
    }

    /// <summary>
    /// 実在しない取引先は <c>null</c>。<b>空の素性を返すと、関門が「未分類だった」と読んで素通しする。</b>
    /// </summary>
    [Fact]
    public async Task 実在しない取引先は_null_で返る()
    {
        using var server = new AccountingServer();

        Assert.Null(await server.Partners.FindProfileAsync(new PartnerId(999)));
    }
}
