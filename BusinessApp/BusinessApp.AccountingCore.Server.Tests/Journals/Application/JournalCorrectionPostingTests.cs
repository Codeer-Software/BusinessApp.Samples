namespace BusinessApp.AccountingCore.Server.Tests.Journals.Application;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO;
using BusinessApp.AccountingCore.Server.Journals.Application;

/// <summary>
/// 再計上（訂正）の計上。
/// </summary>
/// <remarks>
/// <para><b>取消と対になるが、やることは逆である。</b> 取消の中身はサーバが上書きするのに対し、
/// 再計上の中身は利用者が決めるので一切書き換えない。代わりに原仕訳との<b>関係</b>を見る。</para>
/// <para>ここが緩むと、原仕訳が生きたまま再計上が載って<b>取引が帳簿に二重に計上される</b>。
/// 「訂正する」ボタンを通らず、新規作成で種別「訂正」を選んでも同じ経路に来る。</para>
/// </remarks>
public class JournalCorrectionPostingTests
{
    /// <summary>原仕訳（借方 現金 1000 / 貸方 未払金 1000）を計上する。</summary>
    private static JournalEntryId Original(AccountingServer server)
        => server.InsertPosted(1, "5 月分の仕入", "2026-05-20", ("debit", "1100", 1000), ("credit", "2200", 1000));

    /// <summary>再計上の下書きを作り、利用者が入れたつもりの明細を入れる。</summary>
    private static JournalEntryId CorrectionDraft(
        AccountingServer server, JournalEntryId original, long amount = 1200, string postingDate = "2026-08-25")
    {
        var draft = server.InsertCorrectionDraft(original, postingDate: postingDate, transactionDate: "2026-05-20");
        server.InsertLine(draft, 1, "debit", "1100", amount);
        server.InsertLine(draft, 2, "credit", "2200", amount);
        return draft;
    }

    [Fact]
    public async Task 取り消された原仕訳の再計上は計上できる()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));
        var correction = CorrectionDraft(server, original);

        await PostAsync(server, correction);

        var posted = await server.EntryStore.LoadAsync(correction);
        Assert.Equal(EntryStatus.Posted, posted.Status);
        Assert.Equal(EntryType.Correction, posted.EntryType);
        Assert.Equal(3, posted.EntryNo);
        Assert.Equal(original, posted.OriginalEntryId);
    }

    [Fact]
    public async Task 再計上の中身は画面から来たものがそのまま残る()
    {
        // **取消との決定的な違い。** 訂正は「正しい内容」を利用者が決める操作なので、
        // サーバが原仕訳から作り直して上書きしてはいけない。
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));
        var correction = CorrectionDraft(server, original, amount: 1200);

        await PostAsync(server, correction);

        var posted = await server.EntryStore.LoadAsync(correction);
        Assert.Equal([Yen.From(1200), Yen.From(1200)], posted.Lines.Select(l => l.Amount));
        Assert.Equal([DebitCredit.Debit, DebitCredit.Credit], posted.Lines.Select(l => l.DebitCredit));
    }

    [Fact]
    public async Task 取り消されていない原仕訳の再計上は止まる_伝票も採番も残らない()
    {
        // **これを通すと取引が帳簿に二重に載る。** 原仕訳 1000 と再計上 1200 の両方が生きてしまう。
        using var server = new AccountingServer();
        var original = Original(server);
        var correction = CorrectionDraft(server, original);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostAsync(server, correction));

        Assert.Contains(JournalViolationCodes.OriginalNotReversed, error.Violations.Select(v => v.Code));
        Assert.Equal("draft", server.StatusOf(correction));
        Assert.Equal(2, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 二度目の再計上は止まる()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));
        await PostAsync(server, CorrectionDraft(server, original));

        var second = CorrectionDraft(server, original, amount: 1500);
        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostAsync(server, second));

        Assert.Contains(JournalViolationCodes.AlreadyCorrected, error.Violations.Select(v => v.Code));
        Assert.Equal(1L, server.CountAmendments(original, "correction"));
    }

    [Fact]
    public async Task 取消より前の日付では再計上できない()
    {
        // 取消の前に再計上が載ると、その間の期間だけ二重計上になる。
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));   // 計上日は今日（2026-08-24）
        var correction = CorrectionDraft(server, original, postingDate: "2026-08-20");

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostAsync(server, correction));

        Assert.Contains(JournalViolationCodes.CorrectionBeforeReversal, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 取消と同じ日の再計上は通る()
    {
        // 「訂正する」ボタンから来た再計上は、取消と同じ日になるのが普通である。
        using var server = new AccountingServer();
        var original = Original(server);
        var started = await server.AmendAsync(s => s.CorrectAsync(original));
        server.InsertLine(started.CorrectionId, 3, "debit", "1100", 200);
        server.InsertLine(started.CorrectionId, 4, "credit", "2200", 200);

        await PostAsync(server, started.CorrectionId);

        Assert.Equal("posted", server.StatusOf(started.CorrectionId));
    }

    [Fact]
    public async Task 原仕訳を指していない再計上は止める()
    {
        // DDL の CHECK が同じことを禁じているので SQLite からは作れない。
        // それでも守りを置くのは、他の DB や外部からの投入で通ってしまう形だからである。
        using var server = new AccountingServer();
        var draft = Draft() with { OriginalEntryId = null };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => new JournalCorrectionPosting(server.EntryStore).ApplyAsync(draft));

        Assert.Contains(JournalViolationCodes.OriginalEntryMissing, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 計上済みの再計上はもう一度計上できない()
    {
        using var server = new AccountingServer();
        var draft = Draft() with { Status = EntryStatus.Posted };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => new JournalCorrectionPosting(server.EntryStore).ApplyAsync(draft));

        Assert.Contains(JournalViolationCodes.AlreadyPosted, error.Violations.Select(v => v.Code));
    }

    /// <summary>DB を通さずに組み立てた再計上の下書き（到達しない防御を直接叩くため）。</summary>
    private static JournalEntry Draft()
        => new()
        {
            Id = new JournalEntryId(1),
            FiscalYearId = AccountingServer.FiscalYear,
            TransactionDate = new DateOnly(2026, 8, 24),
            PostingDate = new DateOnly(2026, 8, 25),
            Status = EntryStatus.Draft,
            EntryType = EntryType.Correction,
            OriginalEntryId = new JournalEntryId(1),
            EnteredAt = AccountingServer.Now,
            Lines = [],
        };

    private static Task PostAsync(AccountingServer server, JournalEntryId id)
    {
        var entry = SubmitData.Entry(server.Text(id.Value), status: "posted");
        return server.SubmitAsync([SubmitData.Updating(entry)], () => Task.FromResult(new List<ModuleSubmitResult>()));
    }
}
