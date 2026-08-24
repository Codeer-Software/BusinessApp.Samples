namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Periods;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO;

/// <summary>
/// 取消の計上（関門が中身を決める部分）。
/// </summary>
/// <remarks>
/// <b>取消の明細は利用者が決めない。</b> 「原仕訳の貸借を入れ替えたもの」と決まっている
/// （docs/04 §5）ので、画面から何が来ても関門が原仕訳から作り直して上書きする。
/// </remarks>
public class JournalReversalPostingTests
{
    [Fact]
    public async Task 原仕訳の貸借を入れ替えた明細をシステムが入れる()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, ("debit", "1100", 1000), ("credit", "2100", 1000));
        var reversal = server.InsertReversalDraft(original);

        await PostAsync(server, reversal);

        var posted = await server.EntryStore.LoadAsync(reversal);
        Assert.Equal(EntryStatus.Posted, posted.Status);
        Assert.Equal(2, posted.EntryNo);
        Assert.Equal(EntryType.Reversal, posted.EntryType);
        Assert.Equal(original, posted.OriginalEntryId);

        // 借方 1000 / 貸方 1000 が、貸方 1000 / 借方 1000 になる。
        Assert.Equal([DebitCredit.Credit, DebitCredit.Debit], posted.Lines.Select(l => l.DebitCredit));
        Assert.Equal([Yen.From(1000), Yen.From(1000)], posted.Lines.Select(l => l.Amount));
        Assert.Equal(
            [server.AccountOf("1100"), server.AccountOf("2100")],
            posted.Lines.Select(l => l.AccountId));
        Assert.True(posted.IsBalanced);
    }

    [Fact]
    public async Task 画面から送られた明細は使わない()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, ("debit", "1100", 1000), ("credit", "2100", 1000));
        var reversal = server.InsertReversalDraft(original);

        // でたらめな明細を入れておく。取消の中身は利用者が決められない。
        server.InsertLine(reversal, 1, "debit", "1200", 99, taxCategoryCode: "TP");

        await PostAsync(server, reversal);

        var posted = await server.EntryStore.LoadAsync(reversal);
        Assert.Equal(2, posted.Lines.Count);
        Assert.Equal([Yen.From(1000), Yen.From(1000)], posted.Lines.Select(l => l.Amount));
    }

    [Fact]
    public async Task 摘要に何の取消かが残る()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, "5 月分の売上", ("debit", "1100", 500), ("credit", "2100", 500));
        var reversal = server.InsertReversalDraft(original);

        await PostAsync(server, reversal);

        Assert.Equal("伝票番号 1 の取消: 5 月分の売上", (await server.EntryStore.LoadAsync(reversal)).Description);
    }

    [Fact]
    public async Task 二重取消はできず_伝票も採番も残らない()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, ("debit", "1100", 1000), ("credit", "2100", 1000));
        await PostAsync(server, server.InsertReversalDraft(original));

        var second = server.InsertReversalDraft(original);
        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(() => PostAsync(server, second));

        Assert.Contains(JournalViolationCodes.AlreadyReversed, error.Violations.Select(v => v.Code));
        Assert.Equal(EntryStatus.Draft, (await server.EntryStore.LoadAsync(second)).Status);
        Assert.Equal(3, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 原仕訳を指していない取消は止める()
    {
        // **DDL の CHECK が同じことを禁じている**ので、この状態は SQLite からは作れない。
        // それでも守りを置くのは、他の DB や外部からの投入で通ってしまう形だからである。
        // 直接呼んで、通ってしまわないことを確かめる。
        using var server = new AccountingServer();
        var draft = Draft() with { Id = new JournalEntryId(1), OriginalEntryId = null };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.ReversalPosting.ApplyAsync(draft));

        Assert.Contains(JournalViolationCodes.OriginalEntryMissing, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 保存されていない取消には書き込めない()
    {
        using var server = new AccountingServer();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.ReversalPosting.ApplyAsync(Draft() with { Id = null }));

        Assert.Contains("保存されていない", error.Message, StringComparison.Ordinal);
    }

    /// <summary>DB を通さずに組み立てた取消の下書き（到達しない防御を直接叩くため）。</summary>
    private static JournalEntry Draft()
        => new()
        {
            Id = new JournalEntryId(1),
            FiscalYearId = AccountingServer.FiscalYear,
            TransactionDate = new DateOnly(2026, 8, 24),
            PostingDate = new DateOnly(2026, 8, 25),
            Status = EntryStatus.Draft,
            EntryType = EntryType.Reversal,
            OriginalEntryId = new JournalEntryId(1),
            EnteredAt = AccountingServer.Now,
            Lines = [],
        };

    [Fact]
    public async Task 下書きは取り消せない()
    {
        using var server = new AccountingServer();
        var draft = server.InsertDraft();
        server.InsertLine(draft, 1, "debit", "1100", 100);


        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostAsync(server, server.InsertReversalDraft(draft)));

        Assert.Contains(JournalViolationCodes.ReversalTargetNotPosted, error.Violations.Select(v => v.Code));
    }

    private static Task PostAsync(AccountingServer server, JournalEntryId id)
    {
        var entry = SubmitData.Entry(server.Text(id.Value), status: "posted");
        return server.SubmitAsync([SubmitData.Updating(entry)], () => Task.FromResult(new List<ModuleSubmitResult>()));
    }
}
