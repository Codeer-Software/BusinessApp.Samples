namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using Codeer.LowCode.Blazor.DataIO;

/// <summary>
/// 保存時の関門。
/// </summary>
/// <remarks>
/// <b>ここが会計の最後の砦である。</b> 検証を通さずに <c>posted</c> になる道が
/// 1 つでもあれば ADR-0004 が崩れるので、抜け道になりうる形を重点的に置く。
/// </remarks>
public class JournalSubmitGateTests
{
    private const string TemporaryId = "@temporary:0f0a";

    [Fact]
    public void 新規の伝票には入力年月日をシステムが打つ()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId);

        server.Gate.Prepare([SubmitData.Adding(entry)]);

        Assert.Equal(AccountingServer.Now.LocalDateTime, SubmitData.DateTimeValue(entry, "EnteredAt"));
    }

    [Fact]
    public void 既存の伝票の入力年月日には触れない()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry("1");

        server.Gate.Prepare([SubmitData.Updating(entry)]);

        Assert.False(entry.Fields.ContainsKey("EnteredAt"));
    }

    [Fact]
    public void 計上として送られてきた伝票はいったん下書きに戻す()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "posted");

        var pending = server.Gate.Prepare([SubmitData.Adding(entry)]);

        Assert.Equal("draft", SubmitData.SelectValue(entry, "Status"));
        Assert.Equal(TemporaryId, Assert.Single(pending).SubmittedId);
    }

    [Fact]
    public void 下書き保存も状態を送らない保存も計上ではない()
    {
        using var server = new AccountingServer();

        var pending = server.Gate.Prepare(
        [
            SubmitData.Adding(SubmitData.Entry(TemporaryId, status: "draft")),
            SubmitData.Updating(SubmitData.Entry("1")),
        ]);

        Assert.Empty(pending);
    }

    [Fact]
    public void 明細だけの保存を伝票と見間違えない()
    {
        using var server = new AccountingServer();

        // 明細は伝票と同じ Add に混ざって届く（qa/01 F-11）。名前で見分けられなければ、
        // 明細を伝票として計上しようとして壊れる。
        var pending = server.Gate.Prepare(
        [
            SubmitData.Adding(SubmitData.Line(1), SubmitData.Line(2)),
            SubmitData.Updating(SubmitData.Line(3)),
        ]);

        Assert.Empty(pending);
    }

    [Fact]
    public async Task 識別子が差分に載っていない伝票は計上しない()
    {
        using var server = new AccountingServer();
        var entry = new Codeer.LowCode.Blazor.Repository.Data.ModuleData { Name = "JournalEntry" };
        entry.Fields["Status"] = new Codeer.LowCode.Blazor.Repository.Data.SelectFieldData { Value = "posted" };

        var pending = server.Gate.Prepare([SubmitData.Adding(entry)]);

        // 黙って読み飛ばすと「計上したつもりの下書き」が残る。止めて巻き戻す。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Gate.CompleteAsync(pending, [SubmitData.Result()]));
    }

    [Fact]
    public async Task 計上するものが無ければ何もしない()
    {
        using var server = new AccountingServer();

        await server.Gate.CompleteAsync([], [SubmitData.Result()]);

        Assert.Equal(1, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
    }

    [Fact]
    public async Task 保存された伝票を読み直して計上する()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "2100", 1000);

        await CompleteAsync(server, id);

        var entry = await server.EntryStore.LoadAsync(id);
        Assert.Equal(EntryStatus.Posted, entry.Status);
        Assert.Equal(1, entry.EntryNo);
        Assert.Equal(AccountingServer.Now, entry.PostedAt);
        Assert.Equal(2, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 伝票番号は会計年度の中で続き番号になる()
    {
        using var server = new AccountingServer();

        var numbers = new List<int?>();
        for (var i = 0; i < 3; i++)
        {
            var id = server.InsertDraft();
            server.InsertLine(id, 1, "debit", "1100", 100);
            server.InsertLine(id, 2, "credit", "2100", 100);
            await CompleteAsync(server, id);
            numbers.Add((await server.EntryStore.LoadAsync(id)).EntryNo);
        }

        Assert.Equal([1, 2, 3], numbers);
    }

    [Fact]
    public async Task 貸借が合っていない伝票は計上しない()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "2100", 900);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => CompleteAsync(server, id));

        Assert.Contains("一致していない", error.Message, StringComparison.Ordinal);
        Assert.Equal(EntryStatus.Draft, (await server.EntryStore.LoadAsync(id)).Status);
    }

    [Fact]
    public async Task 違反は全件まとめて返す()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        // 明細が無く、かつ貸借も揃わない。1 件だけ見せると直しては弾かれを繰り返す。
        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => CompleteAsync(server, id));

        Assert.NotEmpty(error.Violations);
        Assert.Equal(1, server.Scalar<long>($"select count(*) from journal_entries where id = {id.Value} and status = 'draft'"));
    }

    [Fact]
    public async Task 仮_ID_は保存結果の対応表で本物に読み替える()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        server.InsertLine(id, 2, "credit", "2100", 100);

        await server.Gate.CompleteAsync(
            [new JournalSubmitGate.PendingPosting(TemporaryId)],
            [SubmitData.Result((TemporaryId, id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);

        Assert.Equal(EntryStatus.Posted, (await server.EntryStore.LoadAsync(id)).Status);
    }

    [Fact]
    public async Task 読み替えられない_ID_は止める()
    {
        using var server = new AccountingServer();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Gate.CompleteAsync(
                [new JournalSubmitGate.PendingPosting(TemporaryId)],
                [SubmitData.Result()]));

        Assert.Contains(TemporaryId, error.Message, StringComparison.Ordinal);
    }

    /// <summary>保存が済んだ状態から計上させる（<c>base.SubmitAsync</c> の後に相当）。</summary>
    private static Task CompleteAsync(AccountingServer server, JournalEntryId id)
        => server.Gate.CompleteAsync(
            [new JournalSubmitGate.PendingPosting(id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            [new ModuleSubmitResult()]);
}
