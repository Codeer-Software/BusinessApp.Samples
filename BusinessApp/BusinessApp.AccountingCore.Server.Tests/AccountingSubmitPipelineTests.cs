namespace BusinessApp.AccountingCore.Server.Tests;

using BusinessApp.Partners.Server;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 保存の関門のつなぎ方（<see cref="AccountingSubmitPipeline"/>）。
/// </summary>
/// <remarks>
/// <para><b>つなぎ方そのものを検査する。</b> 本番の配線は <c>BusinessApp.Server</c> にあり、
/// そこは<b>カバレッジにもミューテーションにも載らない</b>（ADR-0012 §3）。
/// 関門を足したのに配線し忘れても、他のテストは全部緑のままになる
/// （2026-08-26 の自己レビュー指摘。qa/02）。</para>
/// <para>ここが落ちるのは「関門が 1 つ外れた」ときだけである。</para>
/// </remarks>
public class AccountingSubmitPipelineTests
{
    private static ModuleData Registration(string no, long partnerId, DateOnly validFrom)
    {
        var data = new ModuleData { Name = PartnerRegistrationSubmitGate.ModuleName };
        data.Fields["RegistrationNo"] = new TextFieldData { Value = no };
        data.Fields["Partner"] = new LinkFieldData
        {
            Value = partnerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        data.Fields["ValidFrom"] = new DateFieldData { Value = validFrom };
        return data;
    }

    private static long InsertPartner(AccountingServer server)
    {
        server.Execute("insert into partners (code, name, is_active) values ('P900', '株式会社ベガ商会', 1)");
        return server.Scalar<long>("select last_insert_rowid()");
    }

    /// <summary>
    /// <b>登録の関門がつながっている。</b> つながっていなければ壊れた番号が保存される。
    /// </summary>
    [Fact]
    public async Task 壊れた登録番号は保存に届かない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server);
        var called = false;

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                    Add = [Registration("12345", partner, new DateOnly(2023, 10, 1))],
                }],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(called);
    }

    /// <summary>
    /// <b>仕訳の関門もつながっている。</b> 計上済みで送られた伝票は、いったん下書きに戻される
    /// （<c>JournalSubmitGate</c> の役目）。
    /// </summary>
    [Fact]
    public async Task 計上済みで送られた伝票は下書きに戻される()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry("1", status: "posted");

        await Assert.ThrowsAnyAsync<Exception>(
            () => server.Pipeline.SubmitAsync(
                [SubmitData.Updating(entry)],
                () => Task.FromResult(new List<ModuleSubmitResult>())));

        Assert.Equal("draft", SubmitData.SelectValue(entry, "Status"));
    }

    /// <summary>
    /// <b>同じ保存に仕訳と登録が混ざっても、どちらの関門も効く。</b>
    /// 片方だけ通す配線になっていたら、ここが落ちる。
    /// </summary>
    [Fact]
    public async Task 仕訳と登録が同じ保存に混ざっても両方の関門が効く()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server);
        var entry = SubmitData.Entry("1", status: "posted");
        var called = false;

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [
                    SubmitData.Updating(entry),
                    new ModuleSubmitData
                    {
                        ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                        Add = [Registration("12345", partner, new DateOnly(2023, 10, 1))],
                    },
                ],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        // 仕訳の関門が先に走って下書きに書き換え、登録の関門が保存を止めた。
        Assert.Equal("draft", SubmitData.SelectValue(entry, "Status"));
        Assert.False(called);
    }

    /// <summary>
    /// <b>取引先の関門もつながっている。</b> つながっていなければ、検査用数字の合わない法人番号が
    /// そのまま保存され、名寄せの自然キーになる（docs/07 §2-2）。
    /// </summary>
    [Fact]
    public async Task 検査用数字の合わない法人番号は保存に届かない()
    {
        using var server = new AccountingServer();
        var partner = new ModuleData { Name = PartnerSubmitGate.ModuleName };
        partner.Fields["CorporateNumber"] = new TextFieldData { Value = "1700110005901" };
        var called = false;

        await Assert.ThrowsAsync<PartnerRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [new ModuleSubmitData { ModuleName = PartnerSubmitGate.ModuleName, Add = [partner] }],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(called);
    }

    [Fact]
    public async Task 保存を渡さなければ止まる()
    {
        using var server = new AccountingServer();

        await Assert.ThrowsAsync<ArgumentNullException>(() => server.Pipeline.SubmitAsync([], null!));
    }
}
