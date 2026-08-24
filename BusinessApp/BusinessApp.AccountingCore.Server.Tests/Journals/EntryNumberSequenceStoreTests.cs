namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

/// <summary>
/// 伝票番号の採番表の読み書き。
/// </summary>
/// <remarks>
/// <b>番号は進めるだけで戻さない</b>（I-17）。ここが壊れると、番号の重複か欠番が出る。
/// どちらも帳簿としては致命的なので、同時計上の検出まで含めて検査する。
/// </remarks>
public class EntryNumberSequenceStoreTests
{
    /// <summary>初期データは第 18 期の採番行を持っている。行が無い状態を作るために消す。</summary>
    private static AccountingServer WithoutSequenceRow()
    {
        var server = new AccountingServer();
        server.Execute("delete from journal_entry_sequences");
        return server;
    }

    [Fact]
    public async Task 行がまだ無い会計年度は_1_番から始まる()
    {
        using var server = WithoutSequenceRow();

        var sequence = await server.SequenceStore.ReadAsync(AccountingServer.FiscalYear);

        Assert.Equal(1, sequence.NextValue);
        Assert.Equal(AccountingServer.FiscalYear, sequence.FiscalYearId);
    }

    [Fact]
    public async Task 行が無ければ作って書き込む()
    {
        using var server = WithoutSequenceRow();
        var sequence = await server.SequenceStore.ReadAsync(AccountingServer.FiscalYear);

        await server.SequenceStore.SaveAsync(sequence, sequence.Allocate().Next);

        Assert.Equal(2, await ReadNextAsync(server));
    }

    [Fact]
    public async Task 二度目からは既にある行を進める()
    {
        using var server = new AccountingServer();
        var first = await server.SequenceStore.ReadAsync(AccountingServer.FiscalYear);
        await server.SequenceStore.SaveAsync(first, first.Allocate().Next);

        var second = await server.SequenceStore.ReadAsync(AccountingServer.FiscalYear);
        await server.SequenceStore.SaveAsync(second, second.Allocate().Next);

        Assert.Equal(2, second.NextValue);
        Assert.Equal(3, await ReadNextAsync(server));
        Assert.Equal(1, server.Scalar<long>("select count(*) from journal_entry_sequences"));
    }

    [Fact]
    public async Task 読んだ後に他の計上が番号を取っていたら書き込まない()
    {
        using var server = new AccountingServer();
        var mine = await server.SequenceStore.ReadAsync(AccountingServer.FiscalYear);

        // 他の計上が先に 1 番を取った。
        await server.SequenceStore.SaveAsync(mine, mine.Allocate().Next);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SequenceStore.SaveAsync(mine, mine.Allocate().Next));

        Assert.Contains("競合", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, await ReadNextAsync(server));
    }

    private static async Task<int> ReadNextAsync(AccountingServer server)
        => (await server.SequenceStore.ReadAsync(AccountingServer.FiscalYear)).NextValue;
}
