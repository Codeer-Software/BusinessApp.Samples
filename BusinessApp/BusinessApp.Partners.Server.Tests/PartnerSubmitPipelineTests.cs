namespace BusinessApp.Partners.Server.Tests;

using System.Globalization;

using BusinessApp.Partners.Server.Tests.Fixtures;
using BusinessApp.TestSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 取引先部品の関門のつなぎ方（<see cref="PartnerSubmitPipeline"/>）。
/// </summary>
/// <remarks>
/// <para><b>つなぎ方そのものを検査する。</b> 本番の配線は CLB のホストにあり、
/// そこは<b>カバレッジにもミューテーションにも載らない</b>（ADR-0012 §3）。
/// 関門を足したのに配線し忘れても、他のテストは全部緑のままになる。</para>
/// <para>ここが落ちるのは「関門が 1 つ外れた」ときだけである。
/// <b>取引先だけを別ホストへ載せるときも同じ組み立てを通す</b>ので、
/// 会計コアを外した配線でも関門が欠けない（ADR-0025 §2）。</para>
/// </remarks>
public class PartnerSubmitPipelineTests
{
    private static long InsertPartner(PartnerServer server)
    {
        server.Execute("insert into partners (code, name, is_active) values ('P900', '株式会社ベガ商会', 1)");
        return server.Scalar<long>("select last_insert_rowid()");
    }

    private static ModuleData Registration(string no, long partnerId)
    {
        var data = new ModuleData { Name = PartnerRegistrationSubmitGate.ModuleName };
        data.Fields["RegistrationNo"] = new TextFieldData { Value = no };
        // 画面が送ってくるのは識別子フィールド（登録は取引先の詳細に置いてある。qa/01 D-17）。
        data.Fields["Partner"] = new IdFieldData
        {
            Value = partnerId.ToString(CultureInfo.InvariantCulture),
        };
        data.Fields["ValidFrom"] = new DateFieldData { Value = new DateOnly(2023, 10, 1) };
        return data;
    }

    private static ModuleData Partner(string corporateNumber)
    {
        var data = new ModuleData { Name = PartnerSubmitGate.ModuleName };
        data.Fields["CorporateNumber"] = new TextFieldData { Value = corporateNumber };
        return data;
    }

    /// <summary>保存が呼ばれたかどうかを見張る。</summary>
    private sealed class SaveSpy
    {
        public bool Called { get; private set; }

        public Task<List<ModuleSubmitResult>> SaveAsync()
        {
            Called = true;
            return Task.FromResult(new List<ModuleSubmitResult>());
        }
    }

    /// <summary><b>登録の関門がつながっている。</b> 外れていれば壊れた番号が保存に届く。</summary>
    [Fact]
    public async Task 壊れた登録番号は保存に届かない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Pipeline(server).SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                    Add = [Registration("12345", partner)],
                }],
                save.SaveAsync));

        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>取引先の関門がつながっている。</b> 外れていれば検査用数字の誤った番号が保存に届く。
    /// </summary>
    /// <remarks>
    /// <b>検査用数字を見るのはこの関門だけである</b>（DB は桁と字種しか見ない。docs/13 §1-2）。
    /// ここが外れると、打ち間違えた番号がそのまま名寄せの自然キーになる。
    /// </remarks>
    [Fact]
    public async Task 検査用数字の誤った法人番号は保存に届かない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Assert.ThrowsAsync<PartnerRejectedException>(
            () => Pipeline(server).SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerSubmitGate.ModuleName,
                    Add = [Partner("1700110005901")],
                }],
                save.SaveAsync));

        Assert.False(save.Called);
    }

    /// <summary>どちらの関門も通る保存は、そのまま保存へ渡る。</summary>
    [Fact]
    public async Task 関門を通る保存はそのまま保存へ渡る()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Pipeline(server).SubmitAsync(
            [new ModuleSubmitData
            {
                ModuleName = PartnerSubmitGate.ModuleName,
                Add = [Partner("8700110005901")],
            }],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>担当外のモジュールだけの保存にも割り込まない。</summary>
    [Fact]
    public async Task 担当外のモジュールの保存は素通しする()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Pipeline(server).SubmitAsync(
            [ForeignModuleData.Adding(ForeignModuleData.Entry("1"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Fact]
    public async Task 引数を渡さなければ止まる()
    {
        using var server = new PartnerServer();
        var pipeline = Pipeline(server);

        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.SubmitAsync([], null!));
    }

    /// <summary><b>本番と同じ組み立て</b>（<see cref="PartnerSubmitPipeline.Create"/>）を通す。</summary>
    private static PartnerSubmitPipeline Pipeline(PartnerServer server)
        => PartnerSubmitPipeline.Create(server.Accessor, SqliteDbAccessor.DataSourceName);
}
