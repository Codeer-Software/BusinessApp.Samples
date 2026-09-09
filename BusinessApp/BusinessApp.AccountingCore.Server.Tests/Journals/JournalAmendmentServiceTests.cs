namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Shared;
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
    /// <summary>取り消される側の仕訳（借方 現金 1000 / 貸方 未払金 1000）。</summary>
    private static JournalEntryId Original(
        AccountingServer server, string transactionDate = "2026-05-20", int entryNo = 1)
        => server.InsertPosted(
            entryNo, "5 月分の仕入", transactionDate, ("debit", "1100", 1000), ("credit", "2200", 1000));

    // --- 複製する（ADR-0048） ---

    /// <summary>
    /// <b>複製した下書きは、DB へ書いて読み戻しても同じ内容である</b>（往復。qa/03 L-14 の処方）。
    /// </summary>
    /// <remarks>
    /// <b>純粋関数が正しくても、書く経路が落とせば意味が無い。</b> 逆に、
    /// 写してはいけない欄を書く経路が拾ってしまうこともある——どちらもここで捕まえる。
    /// </remarks>
    [Fact]
    public async Task 複製した下書きは読み戻しても同じ内容である()
    {
        using var server = new AccountingServer();

        // **写しと制度の版は下書きのうちに入れる。** 計上済みの明細は書き換えられない（I-05）ので、
        // 「写しが付いた計上済み」はこの順でしか作れない。
        var original = server.InsertDraft(
            transactionDate: "2026-05-20", postingDate: "2026-05-20", description: "5 月分の仕入");
        server.InsertLine(original, 1, "debit", "1100", 1000);
        server.InsertLine(original, 2, "credit", "2200", 1000);
        var partner = server.InsertPartner();
        // **写す欄を全部非 NULL にする**（qa/03 L-04 の処方）。
        // NULL のまま往復させると、書く経路が落としていても読み戻しは NULL で一致する。
        server.Execute($"""
            update journal_lines
               set partner_name_snapshot = '株式会社取引先',
                   registration_no_snapshot = 'T1234567890123',
                   applied_rule_version = '2023-10-01',
                   tax_point = '2026-05-20',
                   department_id = (select id from departments where code = '10'),
                   partner_id = {partner},
                   tax_treatment = 'common',
                   item_description = '文房具',
                   book_only_deduction = 'public_transport'
             where journal_entry_id = {original.Value};
            update journal_entries
               set status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00',
                   partner_id = {partner}
             where id = {original.Value};
            """);

        var duplicateId = await server.AmendAsync(s => s.DuplicateAsync(original));
        var copy = await server.EntryStore.LoadAsync(duplicateId);

        Assert.Equal(EntryStatus.Draft, copy.Status);
        Assert.Equal(EntryType.Normal, copy.EntryType);
        Assert.Null(copy.EntryNo);
        Assert.Null(copy.OriginalEntryId);
        Assert.Null(copy.PostedAt);

        // 取引日は原仕訳のまま。計上日は「複製した日」＝今日。
        Assert.Equal(new DateOnly(2026, 5, 20), copy.TransactionDate);
        Assert.Equal(new DateOnly(2026, 8, 24), copy.PostingDate);
        Assert.Equal("5 月分の仕入", copy.Description);

        // 内容はそのまま（貸借は入れ替えない。取消とはここが違う）。
        Assert.Equal([DebitCredit.Debit, DebitCredit.Credit], copy.Lines.Select(l => l.DebitCredit));
        Assert.Equal([Yen.From(1000), Yen.From(1000)], copy.Lines.Select(l => l.Amount));
        Assert.Equal([1, 2], copy.Lines.Select(l => l.LineNo));

        // **非 NULL の欄が、書いて読み戻しても入っている。**
        Assert.NotNull(copy.PartnerId);
        Assert.All(copy.Lines, l => Assert.NotNull(l.DepartmentId));
        Assert.All(copy.Lines, l => Assert.NotNull(l.PartnerId));
        Assert.All(copy.Lines, l => Assert.Equal(TaxTreatment.Common, l.TaxTreatment));
        Assert.All(copy.Lines, l => Assert.Equal("文房具", l.ItemDescription));
        Assert.All(copy.Lines, l => Assert.Equal("public_transport", l.BookOnlyDeduction));

        // **計上時点の写しと制度の版は、書く経路でも落ちている。**
        Assert.All(copy.Lines, l => Assert.Null(l.PartnerNameSnapshot));
        Assert.All(copy.Lines, l => Assert.Null(l.RegistrationNoSnapshot));
        Assert.All(copy.Lines, l => Assert.Null(l.AppliedRuleVersion));

        // **課税仕入れの時点は、書く経路でも写る**（画面に無い欄なので落とすと入れ直せない）。
        Assert.All(copy.Lines, l => Assert.Equal(new DateOnly(2026, 5, 20), l.TaxPoint));
    }

    /// <summary>複製できない種別は「複製できません」で断る（種別の線がサーバまで届く）。</summary>
    /// <remarks>
    /// <b>種別は後から変えられない</b>（DDL のトリガ）ので、その種別で作る。
    /// 決算振替を作る画面も経路もまだ無い（フェーズ 4）が、<b>DDL は値を許している</b>
    /// ので、取込・CLI では作れる。
    /// </remarks>
    [Fact]
    public async Task 決算振替は複製できない()
    {
        using var server = new AccountingServer();
        server.Execute("""
            insert into journal_entries
                (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            values (1, '2026-05-20', '2026-05-20', 'draft', 'closing', '決算振替', '2026-05-20 10:00:00')
            """);
        var original = new JournalEntryId(server.Scalar<long>("select max(id) from journal_entries"));

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.DuplicateAsync(original)));

        // **何と言うかを表明する**（qa/03 L-17。見出しと本文が同じことを 2 回言っていないことも見る）。
        Assert.Equal(
            "複製できません。①種別が「決算振替」の伝票は対象にできません。"
            + "対象にできるのは通常の伝票と訂正・取消です。",
            thrown.Message);
    }

    /// <summary>複製した下書きは、そのまま計上できる（作った下書きが計上の関門を通る）。</summary>
    /// <remarks>
    /// <b>「作れた」と「使える」は別である。</b> 年度や期間の取り違えは、
    /// 計上しようとした瞬間に初めて出る（qa/03 L-30 の型）。
    /// </remarks>
    [Fact]
    public async Task 複製した下書きはそのまま計上できる()
    {
        using var server = new AccountingServer();

        // **伝票番号と識別子をずらす**（既定では両方 1 から並ぶので、
        // 番号を出しているつもりで識別子を出している実装と区別が付かない）。
        server.StartEntryNumbersAt(41);
        var original = Original(server, entryNo: 41);

        var duplicateId = await server.AmendAsync(s => s.DuplicateAsync(original));

        var draft = await server.EntryStore.LoadAsync(duplicateId);
        var context = await server.MasterLoader.LoadAsync();
        var posted = await DbTransactionScope.RunAsync(
            server.Accessor, () => server.Poster.PostAsync(draft, context));

        Assert.Equal(EntryStatus.Posted, posted.Status);
        Assert.Equal(42, posted.EntryNo);
    }

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

        // **内部表現を出さない**（docs/21_画面の原則.md §2）。
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

    // --- 取り消された・訂正されたことを詳細画面に出す（ADR-0027 §3）---

    /// <summary>
    /// 取り消されている伝票は、<b>取消伝票の番号まで返る</b>。
    /// </summary>
    /// <remarks>
    /// 一覧は同じ事実を SQL で引く（ADR-0027 §2）。<b>詳細でも同じ事実が見えること</b>が
    /// この項目を足した理由なので、番号が返らなければ画面は断りを出せない。
    /// </remarks>
    [Fact]
    public async Task 取り消し済みの伝票は取消伝票の番号を返す()
    {
        using var server = new AccountingServer();
        // **識別子と伝票番号をずらす。** 既定では id も entry_no も 1 から並ぶので、
        // `select entry_no` を `select id` に取り違えても全部緑になる（qa/03 L-02）。
        server.StartEntryNumbersAt(101);
        var original = Original(server);
        var reversalId = await server.AmendAsync(s => s.ReverseAsync(original));
        var reversalNo = (await server.EntryStore.LoadAsync(reversalId)).EntryNo;

        var available = await server.AmendmentService.DescribeAsync(original);

        Assert.Equal(reversalNo, available.ReversalEntryNo);
        Assert.Null(available.CorrectionEntryNo);
    }

    /// <summary>訂正まで計上したら、取消と再計上の両方の番号が返る。</summary>
    [Fact]
    public async Task 訂正済みの伝票は取消と訂正の両方の番号を返す()
    {
        using var server = new AccountingServer();
        server.StartEntryNumbersAt(101);
        var original = Original(server);
        var started = await server.AmendAsync(s => s.CorrectAsync(original));
        await PostAsync(server, started.CorrectionId);

        var available = await server.AmendmentService.DescribeAsync(original);

        Assert.Equal((await server.EntryStore.LoadAsync(started.ReversalId)).EntryNo, available.ReversalEntryNo);
        Assert.Equal((await server.EntryStore.LoadAsync(started.CorrectionId)).EntryNo, available.CorrectionEntryNo);
    }

    /// <summary>
    /// <b>訂正の途中で放棄した伝票を「訂正済み」と言わない。</b>
    /// </summary>
    /// <remarks>
    /// 再計上は下書きのまま返る（ADR-0015）。下書きを数えると、
    /// <b>途中で放棄して取消だけが残っている伝票</b>——正当な状態である——が
    /// 訂正済みに見える。
    /// </remarks>
    [Fact]
    public async Task 訂正の下書きは訂正済みに数えない()
    {
        using var server = new AccountingServer();
        server.StartEntryNumbersAt(101);
        var original = Original(server);
        var started = await server.AmendAsync(s => s.CorrectAsync(original));

        var available = await server.AmendmentService.DescribeAsync(original);

        Assert.Equal((await server.EntryStore.LoadAsync(started.ReversalId)).EntryNo, available.ReversalEntryNo);
        Assert.Null(available.CorrectionEntryNo);
    }

    /// <summary>取り消されていない伝票は、どちらの番号も返らない。</summary>
    [Fact]
    public async Task 取り消されていない伝票は番号を返さない()
    {
        using var server = new AccountingServer();

        var available = await server.AmendmentService.DescribeAsync(Original(server));

        Assert.Null(available.ReversalEntryNo);
        Assert.Null(available.CorrectionEntryNo);
    }

    /// <summary>対象が無いときは、番号も分からない（<c>AmendmentAvailability.None</c>）。</summary>
    [Fact]
    public async Task 存在しない伝票は番号も返さない()
    {
        using var server = new AccountingServer();

        var available = await server.AmendmentService.DescribeAsync(new JournalEntryId(999));

        Assert.Null(available.ReversalEntryNo);
        Assert.Null(available.CorrectionEntryNo);
    }

    /// <summary>
    /// <b>今日の会計期間が無くても、取り消されていることは答える。</b>
    /// </summary>
    /// <remarks>
    /// 一覧の逆引きはカレンダーと無関係に出続けるので、ここで落とすと
    /// <b>一覧には「取り消されています」と出るのに、詳細を開くと消える</b>。
    /// ADR-0027 §3 が「一覧と詳細で同じ事実が見える」と決めたことに正面から反する。
    /// </remarks>
    [Fact]
    public async Task 会計期間が無くても取り消されていることは答える()
    {
        using var server = new AccountingServer();
        server.StartEntryNumbersAt(101);
        var original = Original(server);
        var reversalId = await server.AmendAsync(s => s.ReverseAsync(original));
        server.Execute("""
            delete from accounting_periods
            where date(start_date) <= '2026-08-24' and date(end_date) >= '2026-08-24'
            """);

        var available = await server.AmendmentService.DescribeAsync(original);

        Assert.False(available.CanReverse);
        Assert.Contains("会計期間がありません", available.Reason, StringComparison.Ordinal);
        Assert.Equal((await server.EntryStore.LoadAsync(reversalId)).EntryNo, available.ReversalEntryNo);
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
            [server.AccountOf("1100"), server.AccountOf("2200")],
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
            1, "5 月分の仕入", "2026-05-20", partner, ("debit", "1100", 1000), ("credit", "2200", 1000));

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

    // --- 差し戻しの見出し（押したボタンの言葉で断る。docs/21 §2・qa/02 R24-23）---

    /// <summary>
    /// 取消の差し戻しは「取り消せません」で始まる。
    /// </summary>
    /// <remarks>
    /// <b>取消の途中では計上も走る。</b> 既定の見出しのままだと、「取り消す」を押した利用者に
    /// 「計上できません」と返ることになり、頼んだ覚えのない操作を断られる。
    /// </remarks>
    [Fact]
    public async Task 取消の差し戻しは取消の言葉で断る()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.ReverseAsync(original)));

        Assert.StartsWith(JournalPostingRejectedException.ReversalHeadline, error.Message);
        Assert.DoesNotContain(JournalPostingRejectedException.PostingHeadline, error.Message);
    }

    /// <summary>訂正の差し戻しは「訂正できません」で始まる（上と同じ理由）。</summary>
    [Fact]
    public async Task 訂正の差し戻しは訂正の言葉で断る()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.AmendAsync(s => s.ReverseAsync(original));

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.CorrectAsync(original)));

        Assert.StartsWith(JournalPostingRejectedException.CorrectionHeadline, error.Message);
        Assert.DoesNotContain(JournalPostingRejectedException.PostingHeadline, error.Message);
    }

    /// <summary>
    /// 見出しを付け替えても、<b>違反の中身は 1 件も落ちない</b>。
    /// </summary>
    /// <remarks>
    /// 入口で包み直す作りなので、包み方を誤ると違反が消えて「理由の無い差し戻し」になる。
    /// </remarks>
    [Fact]
    public async Task 見出しを付け替えても違反はそのまま残る()
    {
        using var server = new AccountingServer();

        // **違反が 2 件以上出る形で見る。** 1 件だと「先頭だけ持ち直す」「並びを変える」
        // 実装がどちらも緑になる（qa/03 L-02・L-17）。
        // 下書き（計上していない）＋伝票番号が無い＝取消の規則に 2 つ当たる。
        var draft = server.InsertDraft();
        server.InsertLine(draft, 1, "debit", "1100", 100);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.ReverseAsync(draft)));

        Assert.Equal(
            [JournalViolationCodes.AmendmentTargetNotPosted, JournalViolationCodes.AmendmentTargetUnidentified],
            error.Violations.Select(v => v.Code));
        foreach (var violation in error.Violations)
        {
            Assert.Contains(violation.Message, error.Message, StringComparison.Ordinal);
        }

        // 元の例外を内側に残す（どの検証で落ちたかの手掛かり）。
        Assert.IsType<JournalPostingRejectedException>(error.InnerException);
    }

    /// <summary>
    /// <b>取消の計上で落ちても、見出しは「取り消せません」になる。</b>
    /// </summary>
    /// <remarks>
    /// 入口で 1 回包む形にしている理由がここにある。取消は途中で
    /// <c>JournalPoster</c> を通るので、<b>そこが投げると既定の「計上できません」になる</b>——
    /// 利用者は計上を頼んだ覚えが無い（2026-08-31 の自己レビュー）。
    /// 締め済みの期間へ取り消すと、その経路を通る。
    /// </remarks>
    [Fact]
    public async Task 計上の側で落ちても取消の言葉で断る()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        // 取消の計上日は「今日」なので、今日を含む会計期間を締める。
        server.Execute("""
            update accounting_periods set status = 'closed'
            where date(start_date) <= '2026-08-24' and date(end_date) >= '2026-08-24'
            """);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.AmendAsync(s => s.ReverseAsync(original)));

        // **見出しだけを見る。** 本文には「締め済みのため、計上できません。」と出るが、
        // それは**理由の説明として正しい**——取消は計上まで進む操作で、その計上が止まっている。
        // 直すべきは「利用者が頼んでいない操作の名前で断ること」だけである。
        Assert.StartsWith(JournalPostingRejectedException.ReversalHeadline, error.Message);
        Assert.False(error.Message.StartsWith(JournalPostingRejectedException.PostingHeadline, StringComparison.Ordinal));
        Assert.Equal(0L, server.CountAmendments(original, "reversal"));
    }

    /// <summary>画面から「計上」を押したのと同じ経路で計上する。</summary>
    private static Task PostAsync(AccountingServer server, JournalEntryId id)
    {
        var entry = SubmitData.Entry(server.Text(id.Value), status: "posted");
        return server.SubmitAsync([SubmitData.Updating(entry)], () => Task.FromResult(new List<ModuleSubmitResult>()));
    }
}
