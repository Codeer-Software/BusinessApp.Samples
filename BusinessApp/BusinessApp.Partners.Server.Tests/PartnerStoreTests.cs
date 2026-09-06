namespace BusinessApp.Partners.Server.Tests;

using System.Globalization;

using BusinessApp.Partners;
using BusinessApp.Partners.Server;
using BusinessApp.Partners.Server.Tests.Fixtures;

/// <summary>
/// 取引先の素性を読む口（docs/13 §1-2）。
/// </summary>
/// <remarks>
/// 保存の関門は、差分に無い項目をここから補う。<b>読み違えると関門が静かに素通りする。</b>
/// </remarks>
public class PartnerStoreTests
{
    [Fact]
    public async Task 素性をそのまま読み戻せる()
    {
        using var server = new PartnerServer();
        server.Execute("""
            insert into partners (code, name, entity_type, corporate_number)
            values ('P900', '株式会社ベガ商会', 'corporation', '8700110005901')
            """);
        var id = new PartnerId(server.Scalar<long>("select last_insert_rowid()"));

        var profile = await server.Partners.FindProfileAsync(id);

        Assert.Equal(new PartnerProfile(PartnerEntityType.Corporation, "8700110005901"), profile);
    }

    /// <summary>未分類（NULL）を偽の種別で埋めない（docs/13 §1-2）。</summary>
    [Fact]
    public async Task 未分類と未入力は_null_で返る()
    {
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();

        Assert.Null(await server.Partners.FindProfileAsync(new PartnerId(999)));
    }

    // --- 親子まわりの姿（深さ 1 の森。ADR-0028 §2）---
    //
    // **関門経由でしか通っていなかった**（2026-08-31 の自己レビュー R28-14）。
    // 関門のテストは「差し戻されたか」を見るので、`has_children` の 0/1 の読み方や
    // `parent_id` の NULL 変換を取り違えても、**別の理由で差し戻されていれば緑になる**。

    /// <summary>
    /// 親も子も持たない取引先（<b>森の根で、葉でもある</b>）。
    /// </summary>
    /// <remarks>
    /// <b>2 つの値が両方とも「無い」側なので、取り違えても区別が付かない検体である</b>——
    /// だから下の 3 本で、片方だけが「有る」側になる形を必ず通す（qa/03 L-02 の縮退）。
    /// </remarks>
    [Fact]
    public async Task 親も子も無い取引先は両方とも空で返る()
    {
        using var server = new PartnerServer();
        var alone = InsertPartner(server, "P900");

        Assert.Equal(new PartnerLineage(null, false), await server.Partners.FindLineageAsync(alone));
    }

    /// <summary><b>親を持つ側</b>は、親の識別子が入り、子は持たない。</summary>
    [Fact]
    public async Task 親を持つ取引先は親の識別子を返す()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P900");
        var child = InsertPartner(server, "P901", parent);

        Assert.Equal(new PartnerLineage(parent, false), await server.Partners.FindLineageAsync(child));
    }

    /// <summary><b>親になっている側</b>は、自分の親は無いが子は持つ。</summary>
    [Fact]
    public async Task 誰かの親になっている取引先は子を持つと返る()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P900");
        InsertPartner(server, "P901", parent);

        Assert.Equal(new PartnerLineage(null, true), await server.Partners.FindLineageAsync(parent));
    }

    /// <summary>
    /// 実在しない取引先は <c>null</c>。
    /// </summary>
    /// <remarks>
    /// <b><c>PartnerLineage(null, false)</c> と混同しない。</b> 前者は「そんな取引先はいない」、
    /// 後者は「いるが親も子も無い」で、関門の判断が変わる（読めない相手を指した保存は差し戻す）。
    /// </remarks>
    [Fact]
    public async Task 実在しない取引先の親子は_null_で返る()
    {
        using var server = new PartnerServer();

        Assert.Null(await server.Partners.FindLineageAsync(new PartnerId(999)));
    }

    /// <summary>子の種別は<b>重複を除いて</b>返る。未分類（NULL）もそのまま 1 種として返る。</summary>
    [Fact]
    public async Task 子の種別は重複を除いて返る()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P900");
        InsertPartner(server, "P901", parent, "corporation");
        InsertPartner(server, "P902", parent, "corporation");
        InsertPartner(server, "P903", parent);

        var types = await server.Partners.FindChildEntityTypesAsync(parent);

        Assert.Equal([PartnerEntityType.Corporation, null], [.. types.OrderBy(t => t is null)]);
    }

    /// <summary>子が居なければ空。<b><c>null</c> を 1 件返すと「未分類の子が居る」に化ける。</b></summary>
    [Fact]
    public async Task 子が居なければ種別は空で返る()
    {
        using var server = new PartnerServer();

        Assert.Empty(await server.Partners.FindChildEntityTypesAsync(InsertPartner(server, "P900")));
    }

    private static PartnerId InsertPartner(
        PartnerServer server, string code, PartnerId? parent = null, string? entityType = null)
    {
        server.Execute(
            $"""
            insert into partners (code, name, entity_type, parent_partner_id)
            values ('{code}', '{code} 商会',
                    {(entityType is null ? "null" : $"'{entityType}'")},
                    {(parent is PartnerId id ? id.Value.ToString(CultureInfo.InvariantCulture) : "null")})
            """);

        return new PartnerId(server.Scalar<long>("select last_insert_rowid()"));
    }
}
