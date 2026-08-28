namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using Codeer.LowCode.Blazor.DataIO;

/// <summary>
/// 「取り消す」「訂正する」（画面のボタンから Web API 経由で呼ばれる入口）。
/// </summary>
/// <remarks>
/// <b>ここが守るのは「取引が帳簿に二重に載らない」ことである。</b>
/// 訂正は取消と再計上の 2 本組で、取消を計上せずに再計上だけが生まれる道があってはならない
/// （ADR-0015）。
/// </remarks>
public class JournalAmendmentServiceTests
{
    /// <summary>取り消される側の仕訳（借方 現金 1000 / 貸方 買掛金 1000）。</summary>
    private static JournalEntryId Original(AccountingServer server, string transactionDate = "2026-05-20")
        => server.InsertPosted(
            1, "5 月分の仕入", transactionDate, ("debit", "1100", 1000), ("credit", "2100", 1000));

    // --- 取り消す ---

    [Fact]
    public async Task 取り消すと反対仕訳が計上まで進む()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var reversalId = await server.AmendAsync(s => s.ReverseAsync(original));

        var reversal = await server.EntryStore.LoadAsync(reversalId);
        Assert.Equal(EntryStatus.Posted, reversal.Status);
        Assert.Equal(EntryType.Reversal, reversal.EntryType);
        Assert.Equal(original, reversal.OriginalEntryId);
        Assert.Equal(2, reversal.EntryNo);

        // 取引日は原仕訳のまま。計上日は「取り消すと決めた日」＝今日。
        Assert.Equal(new DateOnly(2026, 5, 20), reversal.TransactionDate);
        Assert.Equal(new DateOnly(2026, 8, 24), reversal.PostingDate);

        // 総額方式。貸借だけが入れ替わる。
        Assert.Equal([DebitCredit.Credit, DebitCredit.Debit], reversal.Lines.Select(l => l.DebitCredit));
        Assert.Equal([Yen.From(1000), Yen.From(1000)], reversal.Lines.Select(l => l.Amount));
        Assert.Equal("伝票番号 1 の取消: 5 月分の仕入", reversal.Description);

        // 計上した人は**取消の操作をした人**（原仕訳を計上した人ではない）。
        // 原仕訳は SQL で直接入れており posted_by が NULL なので、写しではないことがここで分かる。
        Assert.Equal(AccountingServer.CurrentUser, reversal.PostedBy);
    }

    [Fact]
    public async Task 原仕訳は取消のあとも計上済みのまま残る()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        await server.AmendAsync(s => s.ReverseAsync(original));

        var kept = await server.EntryStore.LoadAsync(original);
        Assert.Equal(EntryStatus.Posted, kept.Status);
        Assert.Equal(1, kept.EntryNo);
        Assert.Equal(2, kept.Lines.Count);
    }

    [Fact]
    public async Task 二度は取り消せず_伝票も採番も残らない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.ReverseAsync(original)));

        Assert.Contains(JournalViolationCodes.AlreadyReversed, error.Violations.Select(v => v.Code));
        Assert.Equal(1L, server.CountAmendments(original, "reversal"));
        Assert.Equal(0L, server.CountAmendments(original, "reversal", status: "draft"));
        Assert.Equal(3, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 存在しない仕訳は取り消せない()
    {
        using var server = new AccountingServer();

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.ReverseAsync(new JournalEntryId(999))));

        Assert.Contains(JournalViolationCodes.AmendmentTargetNotFound, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 下書きは取り消せない()
    {
        using var server = new AccountingServer();
        var draft = server.InsertDraft();
        server.InsertLine(draft, 1, "debit", "1100", 100);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.ReverseAsync(draft)));

        Assert.Contains(JournalViolationCodes.AmendmentTargetNotPosted, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 今日に対応する会計期間がなければ取り消せない()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        // 「今日」（2026-08-24）を含む会計期間を消す。年度末の翌日に取り消そうとした状況と同じ。
        server.Execute("""
            delete from accounting_periods
            where date(start_date) <= '2026-08-24' and date(end_date) >= '2026-08-24'
            """);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.ReverseAsync(original)));

        Assert.Contains(JournalViolationCodes.PeriodNotFound, error.Violations.Select(v => v.Code));
        Assert.Equal(0L, server.CountAmendments(original, "reversal", status: "draft"));
    }

    // --- できることを調べる（画面のボタンの出し分け）---

    [Fact]
    public async Task 計上済みの通常の伝票は取消も訂正もできる()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var available = await server.AmendmentService.DescribeAsync(original);

        Assert.True(available.CanReverse);
        Assert.True(available.CanCorrect);
        Assert.Equal(string.Empty, available.Reason);
    }

    [Fact]
    public async Task 取消の伝票は取消も訂正もできず_理由が返る()
    {
        // **押しても失敗するボタンを出さないための API である**（2026-08-25 の指摘）。
        using var server = new AccountingServer();
        var reversalId = await server.AmendAsync(s => s.ReverseAsync(Original(server)));

        var available = await server.AmendmentService.DescribeAsync(reversalId);

        Assert.False(available.CanReverse);
        Assert.False(available.CanCorrect);
        Assert.Contains("取消", available.Reason, StringComparison.Ordinal);

        // **内部表現を出さない**（docs/09_画面の原則.md §2）。
        Assert.DoesNotContain("Reversal", available.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 取り消し済みの伝票は取消も訂正もできない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));

        var available = await server.AmendmentService.DescribeAsync(original);

        Assert.False(available.CanReverse);
        Assert.False(available.CanCorrect);
        Assert.Contains("既に取り消されています", available.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 訂正した伝票そのものは_また訂正できる()
    {
        // 直した内容がまた誤っていたときに詰まないこと（ADR-0015）。
        // **訂正された「元の伝票」ではなく、訂正の伝票のほうが次の入口になる。**
        using var server = new AccountingServer();
        var original = Original(server);
        var started = await server.AmendAsync(s => s.CorrectAsync(original));
        await PostAsync(server, started.CorrectionId);

        Assert.False((await server.AmendmentService.DescribeAsync(original)).CanCorrect);

        var available = await server.AmendmentService.DescribeAsync(started.CorrectionId);
        Assert.True(available.CanReverse);
        Assert.True(available.CanCorrect);
    }

    [Fact]
    public async Task 下書きは取消も訂正もできない()
    {
        using var server = new AccountingServer();

        var available = await server.AmendmentService.DescribeAsync(server.InsertDraft());

        Assert.False(available.CanReverse);
        Assert.False(available.CanCorrect);
    }

    [Fact]
    public async Task 存在しない伝票は取消も訂正もできない()
    {
        using var server = new AccountingServer();

        var available = await server.AmendmentService.DescribeAsync(new JournalEntryId(999));

        Assert.False(available.CanReverse);
        Assert.False(available.CanCorrect);
        Assert.Contains("見つかりません", available.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 今日に対応する会計期間がなければ何もできない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        server.Execute("""
            delete from accounting_periods
            where date(start_date) <= '2026-08-24' and date(end_date) >= '2026-08-24'
            """);

        var available = await server.AmendmentService.DescribeAsync(original);

        Assert.False(available.CanReverse);
        Assert.False(available.CanCorrect);
        Assert.Contains("会計期間がありません", available.Reason, StringComparison.Ordinal);
    }

    // --- 訂正する ---

    [Fact]
    public async Task 訂正すると_取消は計上され_再計上は下書きで残る()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var started = await server.AmendAsync(s => s.CorrectAsync(original));

        var reversal = await server.EntryStore.LoadAsync(started.ReversalId);
        Assert.Equal(EntryStatus.Posted, reversal.Status);
        Assert.Equal(EntryType.Reversal, reversal.EntryType);

        // **再計上は計上しない。** 中身は利用者が決めるので、下書きのまま開いて直させる。
        var correction = await server.EntryStore.LoadAsync(started.CorrectionId);
        Assert.Equal(EntryStatus.Draft, correction.Status);
        Assert.Equal(EntryType.Correction, correction.EntryType);
        Assert.Null(correction.EntryNo);

        // どちらも**原仕訳を**指す。2 本を結ぶ列は持たない（ADR-0015）。
        Assert.Equal(original, reversal.OriginalEntryId);
        Assert.Equal(original, correction.OriginalEntryId);
    }

    [Fact]
    public async Task 再計上には原仕訳の内容がそのまま写る()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var started = await server.AmendAsync(s => s.CorrectAsync(original));

        var correction = await server.EntryStore.LoadAsync(started.CorrectionId);

        // 貸借は**入れ替えない**。利用者は誤っている箇所だけを直せばよい。
        Assert.Equal([DebitCredit.Debit, DebitCredit.Credit], correction.Lines.Select(l => l.DebitCredit));
        Assert.Equal([Yen.From(1000), Yen.From(1000)], correction.Lines.Select(l => l.Amount));
        Assert.Equal(
            [server.AccountOf("1100"), server.AccountOf("2100")],
            correction.Lines.Select(l => l.AccountId));

        Assert.Equal(new DateOnly(2026, 5, 20), correction.TransactionDate);
        Assert.Equal(new DateOnly(2026, 8, 24), correction.PostingDate);
        Assert.Equal("伝票番号 1 の訂正: 5 月分の仕入", correction.Description);
    }

    [Fact]
    public async Task 伝票番号は取消にだけ出る()
    {
        // 下書きは番号を持たない（I-17）。訂正を放棄しても番号は 1 つしか消費されない。
        using var server = new AccountingServer();
        var original = Original(server);

        var started = await server.AmendAsync(s => s.CorrectAsync(original));

        Assert.Equal(2, (await server.EntryStore.LoadAsync(started.ReversalId)).EntryNo);
        Assert.Null((await server.EntryStore.LoadAsync(started.CorrectionId)).EntryNo);
        Assert.Equal(3, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 訂正できないときは取消も残らない()
    {
        // **1 操作である以上、途中の状態を残さない。** 取消だけが計上されて
        // 「訂正しようとしたのに取り消されただけ」になるのが最悪である。
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.CorrectAsync(original)));

        Assert.Contains(JournalViolationCodes.AlreadyReversed, error.Violations.Select(v => v.Code));
        Assert.Equal(1L, server.CountAmendments(original, "reversal"));
        Assert.Equal(0L, server.CountAmendments(original, "correction", status: "draft"));
    }

    [Fact]
    public async Task 訂正の伝票を訂正できる()
    {
        // 直した内容がまた誤っていたときに詰まないこと（ADR-0015）。
        using var server = new AccountingServer();
        var original = Original(server);
        var started = await server.AmendAsync(s => s.CorrectAsync(original));

        // 利用者が中身を直して計上したのと同じ状態にする。**保存経路も本物を通す。**
        await PostAsync(server, started.CorrectionId);

        // 訂正（再計上）の経路でも計上した人が入る。種別ごとの分岐（PostAsync のホワイトリスト）が
        // 増減したときに、訂正だけ落ちても気づけるようにここで固定する。
        Assert.Equal(
            AccountingServer.CurrentUser,
            (await server.EntryStore.LoadAsync(started.CorrectionId)).PostedBy);

        var again = await server.AmendAsync(s => s.CorrectAsync(started.CorrectionId));

        Assert.Equal(EntryStatus.Posted, (await server.EntryStore.LoadAsync(again.ReversalId)).Status);
        Assert.Equal(started.CorrectionId, (await server.EntryStore.LoadAsync(again.CorrectionId)).OriginalEntryId);
    }

    /// <summary>計上した人は「そのとき操作した人」であり、原仕訳の値の写しでも固定値でもない。</summary>
    [Fact]
    public async Task 別の人が取り消すと取消にはその人が入る()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        server.CurrentUserId = "92";
        var reversalId = await server.AmendAsync(s => s.ReverseAsync(original));

        Assert.Equal(92, (await server.EntryStore.LoadAsync(reversalId)).PostedBy);
        // 原仕訳（SQL で直接入れたもの）は NULL のまま。取消の操作で書き換わらない。
        Assert.Null((await server.EntryStore.LoadAsync(original)).PostedBy);
    }

    [Fact]
    public async Task 取引先は取消にも再計上にも写る()
    {
        // 帳簿の法定記載事項①（取引先）が取消・訂正で落ちると、
        // 補助元帳から取引先で辿ったときに反対仕訳だけが見つからなくなる。
        using var server = new AccountingServer();
        var partner = server.InsertPartner();
        var original = server.InsertPosted(
            1, "5 月分の仕入", "2026-05-20", partner, ("debit", "1100", 1000), ("credit", "2100", 1000));

        var started = await server.AmendAsync(s => s.CorrectAsync(original));

        Assert.Equal(partner, (await server.EntryStore.LoadAsync(started.ReversalId)).PartnerId!.Value.Value);
        Assert.Equal(partner, (await server.EntryStore.LoadAsync(started.CorrectionId)).PartnerId!.Value.Value);
    }

    [Fact]
    public async Task 取消の伝票は訂正できない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        var reversalId = await server.AmendAsync(s => s.ReverseAsync(original));

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.CorrectAsync(reversalId)));

        Assert.Contains(JournalViolationCodes.AmendmentTargetNotAmendable, error.Violations.Select(v => v.Code));
    }

    /// <summary>画面から「計上」を押したのと同じ経路で計上する。</summary>
    private static Task PostAsync(AccountingServer server, JournalEntryId id)
    {
        var entry = SubmitData.Entry(server.Text(id.Value), status: "posted");
        return server.SubmitAsync([SubmitData.Updating(entry)], () => Task.FromResult(new List<ModuleSubmitResult>()));
    }
}
