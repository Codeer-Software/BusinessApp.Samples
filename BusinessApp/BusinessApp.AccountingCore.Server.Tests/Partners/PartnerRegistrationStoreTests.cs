namespace BusinessApp.AccountingCore.Server.Tests.Partners;

using BusinessApp.AccountingCore.Partners;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

/// <summary>
/// 取引先の名称と登録を読む口。<b>期間の判定はここでしない</b>ことも含めて固定する。
/// </summary>
public class PartnerRegistrationStoreTests
{
    private static long InsertPartner(AccountingServer server, string code, string name)
    {
        server.Execute($"insert into partners (code, name, is_active) values ('{code}', '{name}', 1)");
        return server.Scalar<long>("select last_insert_rowid()");
    }

    [Fact]
    public async Task 取引先の名称を読む()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");

        Assert.Equal("株式会社ベガ商会", await server.Registrations.FindNameAsync(new PartnerId(partner)));
    }

    /// <summary>
    /// 居ない取引先は <c>null</c>。<b>外部キーがあるので仕訳からは起きない</b>が、
    /// この口は識別子を選ばないので、無いものを渡されたときの答えを決めておく。
    /// </summary>
    [Fact]
    public async Task 居ない取引先は_null()
    {
        using var server = new AccountingServer();

        Assert.Null(await server.Registrations.FindNameAsync(new PartnerId(999_999)));
    }

    [Fact]
    public async Task 登録が無ければ空を返す()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");

        Assert.Empty(await server.Registrations.LoadRegistrationsAsync(new PartnerId(partner)));
    }

    /// <summary>
    /// <b>終わった登録も読む。</b> 絞り込みを SQL に書くと、境界の規則が
    /// <see cref="InvoiceRegistrationHistory"/> と 2 か所に分かれる。
    /// </summary>
    [Fact]
    public async Task 終わった登録も読む()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from, ended_on, end_reason)
            values ({partner}, 'T1111111111111', {AccountingServer.DateLiteral("2023-10-01")}, {AccountingServer.DateLiteral("2026-03-31")}, 'revoked')
            """);
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, 'T2222222222222', {AccountingServer.DateLiteral("2026-04-01")})
            """);

        var registrations = await server.Registrations.LoadRegistrationsAsync(new PartnerId(partner));

        Assert.Equal(2, registrations.Count);
        Assert.Contains(
            registrations,
            r => r.RegistrationNo == "T1111111111111"
                 && r.ValidFrom == new DateOnly(2023, 10, 1)
                 && r.EndedOn == new DateOnly(2026, 3, 31));
        Assert.Contains(
            registrations,
            r => r.RegistrationNo == "T2222222222222" && r.EndedOn is null);
    }

    [Fact]
    public async Task 登録の行から取引先を引ける()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, 'T1111111111111', {AccountingServer.DateLiteral("2023-10-01")})
            """);
        var row = server.Scalar<long>("select last_insert_rowid()");

        Assert.Equal(new PartnerId(partner), await server.Registrations.FindPartnerOfAsync(row));
    }

    /// <summary>
    /// 居ない行は <c>null</c>。<b>新規作成の保存では行がまだ無い</b>ので、この答えが要る。
    /// </summary>
    [Fact]
    public async Task 居ない登録の行は_null()
    {
        using var server = new AccountingServer();

        Assert.Null(await server.Registrations.FindPartnerOfAsync(999_999));
    }
}
