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
/// （docs/10 §5）ので、画面から何が来ても関門が原仕訳から作り直して上書きする。
/// </remarks>
public class JournalReversalPostingTests
{
    [Fact]
    public async Task 原仕訳の貸借を入れ替えた明細をシステムが入れる()
    {
        using var server = new AccountingServer();
        // **原仕訳と取消で取引日を変える。** 同じ日にすると、取引日が書き戻されていない
        // という欠陥がテストから見えなくなる（実際にそれで見逃した）。
        var original = server.InsertPosted(
            1, null, "2026-05-20", ("debit", "1100", 1000), ("credit", "2200", 1000));
        var reversal = server.InsertReversalDraft(original);

        await PostAsync(server, reversal);

        var posted = await server.EntryStore.LoadAsync(reversal);
        Assert.Equal(EntryStatus.Posted, posted.Status);

        // 取引日は原仕訳のまま。計上日だけが後ろにずれる（docs/10 §5）。
        Assert.Equal(new DateOnly(2026, 5, 20), posted.TransactionDate);
        Assert.Equal(new DateOnly(2026, 8, 25), posted.PostingDate);
        Assert.Equal(2, posted.EntryNo);
        Assert.Equal(EntryType.Reversal, posted.EntryType);
        Assert.Equal(original, posted.OriginalEntryId);

        // 借方 1000 / 貸方 1000 が、貸方 1000 / 借方 1000 になる。
        Assert.Equal([DebitCredit.Credit, DebitCredit.Debit], posted.Lines.Select(l => l.DebitCredit));
        Assert.Equal([Yen.From(1000), Yen.From(1000)], posted.Lines.Select(l => l.Amount));
        Assert.Equal(
            [server.AccountOf("1100"), server.AccountOf("2200")],
            posted.Lines.Select(l => l.AccountId));
        Assert.True(posted.IsBalanced);
    }

    [Fact]
    public async Task 規則より前に計上された補助科目つきの伝票も取り消せる()
    {
        // **これが「取消では止めない」の唯一の根拠である**（ADR-0038 §3。docs/10 §6）。
        // 補助科目を使わない科目に補助科目が付いた計上済みの伝票が稼働 DB に 1 行あり（伝票 36）、
        // **それを取り消せなくなると ADR-0004 の「取消できない伝票を作らない」が破れる。**
        // 関門（`JournalEntryValidator`）と DDL のトリガの<b>両方</b>を通す形で確かめる——
        // 片方ずつの検査は済んでいるが、配線は誰も見ていなかった（qa/03 L-01 の型。自己レビューで指摘）。
        using var server = new AccountingServer();
        var original = server.InsertPostedWithSubAccountOnUnusedAccount(1, "2026-05-20");
        var reversal = server.InsertReversalDraft(original);

        await PostAsync(server, reversal);

        var posted = await server.EntryStore.LoadAsync(reversal);
        Assert.Equal(EntryStatus.Posted, posted.Status);

        // **取消にも補助科目が写っている。** 原仕訳の写しなので、ここが空になるのは誤りである。
        Assert.Equal(
            server.SubAccountOf("1100", "X001"),
            posted.Lines.Single(l => l.LineNo == 1).SubAccountId);
    }

    [Fact]
    public async Task 規則より前に計上された取引先の無い伝票も取り消せる()
    {
        // **これが「取消では止めない」の唯一の根拠である**（docs/10 §6-2。docs/04 §1 の A-4）。
        // 取引先を要する科目に取引先の無い計上済みの明細が稼働 DB に実在し（件数と数え方は qa/04）、
        // **それらを取り消せなくなると ADR-0004 の「取消できない伝票を作らない」が破れる。**
        // 関門（`JournalEntryValidator`）と DDL のトリガの<b>両方</b>を通す形で確かめる——
        // 片方ずつの検査では配線を誰も見ない（qa/03 L-01 の型。qa/02 R53-05 と同じ処方）。
        using var server = new AccountingServer();
        var original = server.InsertPostedWithoutPartnerOnRequiringAccount(1, "2026-05-20");
        var reversal = server.InsertReversalDraft(original);

        await PostAsync(server, reversal);

        var posted = await server.EntryStore.LoadAsync(reversal);
        Assert.Equal(EntryStatus.Posted, posted.Status);

        // **取消にも取引先が無いまま写っている。** 空欄を勝手に埋めると、
        // 帳簿に原仕訳と食い違う相手方が載る（ADR-0018）。
        Assert.Null(posted.PartnerId);
        Assert.All(posted.Lines, l => Assert.Null(l.PartnerId));
    }

    [Fact]
    public async Task 画面から送られた明細は使わない()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 1000), ("credit", "2200", 1000));
        var reversal = server.InsertReversalDraft(original);

        // でたらめな明細を入れておく。取消の中身は利用者が決められない。
        server.InsertLine(reversal, 1, "debit", "1200", 99, taxCategoryCode: "TP");

        await PostAsync(server, reversal);

        var posted = await server.EntryStore.LoadAsync(reversal);
        Assert.Equal(2, posted.Lines.Count);
        Assert.Equal([Yen.From(1000), Yen.From(1000)], posted.Lines.Select(l => l.Amount));

        // でたらめ行の税区分（課税仕入）が残っていないこと。原仕訳の「対象外」に戻る。
        Assert.All(posted.Lines, l => Assert.Equal(server.TaxCategoryOf("OUT"), l.TaxCategoryId));
    }

    [Fact]
    public async Task 取引先も原仕訳から写る()
    {
        // 帳簿の法定記載事項①（取引先）が取消で落ちると、
        // 取引先から辿ったときに反対仕訳だけが見つからなくなる。
        using var server = new AccountingServer();
        var partner = server.InsertPartner();
        var original = server.InsertPosted(
            1, null, "2026-05-20", partner, ("debit", "1100", 300), ("credit", "2200", 300));
        var reversal = server.InsertReversalDraft(original);

        await PostAsync(server, reversal);

        Assert.Equal(partner, (await server.EntryStore.LoadAsync(reversal)).PartnerId!.Value.Value);
    }

    [Fact]
    public async Task 摘要に何の取消かが残る()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, "5 月分の売上", "2026-05-20", ("debit", "1100", 500), ("credit", "2200", 500));
        var reversal = server.InsertReversalDraft(original);

        await PostAsync(server, reversal);

        Assert.Equal("伝票番号 1 の取消: 5 月分の売上", (await server.EntryStore.LoadAsync(reversal)).Description);
    }

    [Fact]
    public async Task 二重取消はできず_伝票も採番も残らない()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 1000), ("credit", "2200", 1000));
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
        var context = await server.MasterLoader.LoadAsync();

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => new JournalReversalPosting(server.EntryStore).ApplyAsync(draft, context));

        Assert.Contains(JournalViolationCodes.OriginalEntryMissing, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 計上済みの取消はもう一度計上できない()
    {
        // 通してもトリガが生の SQLite 例外を出すだけで、利用者には何も伝わらない。
        using var server = new AccountingServer();
        var context = await server.MasterLoader.LoadAsync();

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => new JournalReversalPosting(server.EntryStore)
                .ApplyAsync(Draft() with { Status = EntryStatus.Posted }, context));

        Assert.Contains(JournalViolationCodes.AlreadyPosted, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 計上日に対応する会計期間がなければ取り消せない()
    {
        // 会計年度は**取消の計上日**から引く。年度の外に落ちる日付では取り消せない。
        using var server = new AccountingServer();
        var context = await server.MasterLoader.LoadAsync();

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => new JournalReversalPosting(server.EntryStore)
                .ApplyAsync(Draft() with { PostingDate = new DateOnly(2030, 1, 1) }, context));

        Assert.Contains(JournalViolationCodes.PeriodNotFound, error.Violations.Select(v => v.Code));
    }

    [Fact]
    public async Task 保存されていない取消には書き込めない()
    {
        using var server = new AccountingServer();

        var context = await server.MasterLoader.LoadAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new JournalReversalPosting(server.EntryStore).ApplyAsync(Draft() with { Id = null }, context));

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

        Assert.Contains(JournalViolationCodes.AmendmentTargetNotPosted, error.Violations.Select(v => v.Code));
    }

    private static Task PostAsync(AccountingServer server, JournalEntryId id)
    {
        var entry = SubmitData.Entry(server.Text(id.Value), status: "posted");
        return server.SubmitAsync([SubmitData.Updating(entry)], () => Task.FromResult(new List<ModuleSubmitResult>()));
    }
}
