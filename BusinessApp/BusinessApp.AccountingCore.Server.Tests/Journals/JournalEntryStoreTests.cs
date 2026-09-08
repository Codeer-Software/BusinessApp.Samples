namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Accounts;
using BusinessApp.AccountingCore.Departments;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.Partners;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 保存済みの仕訳の読み書き。
/// </summary>
/// <remarks>
/// 列名の綴りと NULL の扱いを、本物の DDL に対して確かめる。
/// ここが黙って null を返すと、検証は「値が無い」ものとして通ってしまう。
/// </remarks>
public class JournalEntryStoreTests
{
    [Fact]
    public async Task 伝票と明細を丸ごと読む()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft(transactionDate: "2026-08-20", postingDate: "2026-08-24");
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "2200", 1000);

        var entry = await server.EntryStore.LoadAsync(id);

        Assert.Equal(id, entry.Id);
        Assert.Equal(AccountingServer.FiscalYear, entry.FiscalYearId);
        Assert.Equal(new DateOnly(2026, 8, 20), entry.TransactionDate);
        Assert.Equal(new DateOnly(2026, 8, 24), entry.PostingDate);
        Assert.Equal(EntryStatus.Draft, entry.Status);
        Assert.Equal(EntryType.Normal, entry.EntryType);
        // JST 固定で読む（DatabaseTimeZone）。マシンのタイムゾーンに依存させない。
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.FromHours(9)), entry.EnteredAt);
        Assert.True(entry.IsBalanced);
        Assert.Equal([1, 2], entry.Lines.Select(l => l.LineNo));
        Assert.Equal([DebitCredit.Debit, DebitCredit.Credit], entry.Lines.Select(l => l.DebitCredit));
        Assert.Equal(Yen.From(1000), entry.Lines[0].Amount);
    }

    [Fact]
    public async Task 下書きでは伝票番号も計上日時も入っていない()
    {
        using var server = new AccountingServer();

        // **摘要も null で入れる。** ここは「任意の欄が NULL のまま読み戻せるか」を見るテストで、
        // 既定の摘要が入ると description の NULL 読みだけ検査されなくなる（docs/10 §4-2-1）。
        var id = server.InsertDraft(description: null);

        var entry = await server.EntryStore.LoadAsync(id);

        Assert.Null(entry.EntryNo);
        Assert.Null(entry.PostedAt);
        Assert.Null(entry.PostedBy);
        Assert.Null(entry.Description);
        Assert.Null(entry.PartnerId);
        Assert.Null(entry.OriginalEntryId);
        Assert.Null(entry.SourceComponent);
        Assert.Null(entry.SourceDocumentId);
        Assert.Null(entry.IdempotencyKey);
        Assert.Empty(entry.Lines);
    }

    [Fact]
    public async Task 入っている任意項目はそのまま読める()
    {
        using var server = new AccountingServer();
        var original = server.InsertDraft();
        var id = server.InsertDraft(entryType: "reversal", originalEntryId: original);
        server.InsertPartner();
        server.InsertSubAccount("6110");

        server.Execute($"""
            update journal_entries
               set description = '売上の取消',
                   partner_id = (select id from partners limit 1),
                   source_component = 'Expense', source_document_id = 'EXP-1', idempotency_key = 'K-1'
             where id = {id.Value}
            """);
        server.InsertLine(id, 1, "debit", "6110", 500, taxCategoryCode: "TP", departmentCode: "20");
        server.Execute($"""
            update journal_lines
               set sub_account_id = (select id from sub_accounts limit 1),
                   partner_id = (select id from partners limit 1),
                   partner_name_snapshot = '株式会社れい', tax_treatment = 'for_taxable_sales',
                   tax_point = '2026-08-24', applied_rule_version = 'tax-2026-10',
                   item_description = '事務用品', book_only_deduction = 'public_transport',
                   evidence_ref = 'DOC-1'
             where journal_entry_id = {id.Value}
            """);

        var entry = await server.EntryStore.LoadAsync(id);
        var line = Assert.Single(entry.Lines);

        Assert.Equal(EntryType.Reversal, entry.EntryType);
        Assert.Equal(original, entry.OriginalEntryId);
        Assert.Equal("売上の取消", entry.Description);
        Assert.Equal("Expense", entry.SourceComponent);
        Assert.Equal("EXP-1", entry.SourceDocumentId);
        Assert.Equal("K-1", entry.IdempotencyKey);
        Assert.Equal("株式会社れい", line.PartnerNameSnapshot);
        Assert.Equal(TaxTreatment.ForTaxableSales, line.TaxTreatment);
        Assert.Equal(new DateOnly(2026, 8, 24), line.TaxPoint);
        Assert.Equal(new RuleVersion("tax-2026-10"), line.AppliedRuleVersion);
        Assert.Equal("事務用品", line.ItemDescription);
        Assert.Equal("public_transport", line.BookOnlyDeduction);
        Assert.Equal("DOC-1", line.EvidenceRef);
        Assert.False(line.IsTaxLine);
        Assert.Null(line.ParentLineNo);
        Assert.NotNull(line.SubAccountId);
        Assert.NotNull(line.DepartmentId);
        Assert.NotNull(line.PartnerId);
    }

    [Fact]
    public async Task 消費税行は親行の行番号を持つ()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "6110", 1000, taxCategoryCode: "TP", departmentCode: "20");
        server.InsertLine(id, 2, "debit", "1540", 100);
        server.Execute($"update journal_lines set is_tax_line = 1, parent_line_no = 1 where journal_entry_id = {id.Value} and line_no = 2");

        var entry = await server.EntryStore.LoadAsync(id);

        Assert.True(entry.Lines[1].IsTaxLine);
        Assert.Equal(1, entry.Lines[1].ParentLineNo);
    }

    [Fact]
    public async Task 無い伝票を読もうとしたら止まる()
    {
        using var server = new AccountingServer();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.EntryStore.LoadAsync(new JournalEntryId(999)));

        Assert.Contains("見つからない", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 計上の印を付けると番号と計上日時と計上した人と版が入る()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        await server.EntryStore.MarkPostedAsync(
            id, new EntryNumber(AccountingServer.FiscalYear, 7), AccountingServer.Now, AccountingServer.CurrentUser);

        var entry = await server.EntryStore.LoadAsync(id);
        Assert.Equal(EntryStatus.Posted, entry.Status);
        Assert.Equal(7, entry.EntryNo);
        Assert.Equal(AccountingServer.Now, entry.PostedAt);
        Assert.Equal(AccountingServer.CurrentUser, entry.PostedBy);

        // 版は CLB が進めるもの。ここで進めると 1 回の保存で 2 つ進み、画面が古い版を握る。
        Assert.Equal(0, server.Scalar<long>($"select optimistic_locking from journal_entries where id = {id.Value}"));
    }

    /// <summary>「誰か分からない」は偽の値で埋めず NULL のまま持つ（この列より前の伝票と同じ扱い）。</summary>
    [Fact]
    public async Task 計上した人が分からないときはNULLのまま読み戻せる()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        await server.EntryStore.MarkPostedAsync(
            id, new EntryNumber(AccountingServer.FiscalYear, 7), AccountingServer.Now, null);

        var entry = await server.EntryStore.LoadAsync(id);
        Assert.Equal(EntryStatus.Posted, entry.Status);
        Assert.Null(entry.PostedBy);
    }

    [Fact]
    public async Task 計上済みには二度と印を付けられない()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        await server.EntryStore.MarkPostedAsync(
            id, new EntryNumber(AccountingServer.FiscalYear, 1), AccountingServer.Now, AccountingServer.CurrentUser);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.EntryStore.MarkPostedAsync(
                id, new EntryNumber(AccountingServer.FiscalYear, 2), AccountingServer.Now, AccountingServer.CurrentUser));

        Assert.Contains("下書きではない", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.Scalar<long>($"select entry_no from journal_entries where id = {id.Value}"));
    }

    [Fact]
    public async Task 明細を入れ替えると_全項目がそのまま読み戻せる()
    {
        // **書いた値が読み戻せることを 1 件で固定する。** 列を 1 つ取り違えても、
        // 貸借一致でも金額でも検出できない（NULL のまま静かに落ちる）。
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        var partnerId = server.InsertPartner();
        var subAccountId = server.InsertSubAccount("1200");

        var line = new JournalLine
        {
            LineNo = 7,
            DebitCredit = DebitCredit.Credit,
            AccountId = server.AccountOf("1200"),
            SubAccountId = new SubAccountId(subAccountId),
            DepartmentId = server.DepartmentOf("20"),
            PartnerId = new PartnerId(partnerId),
            PartnerNameSnapshot = "株式会社れい",
            Amount = Yen.From(12_345),
            TaxCategoryId = server.TaxCategoryOf("TP"),
            TaxTreatment = TaxTreatment.ForTaxableSales,
            TaxPoint = new DateOnly(2026, 10, 1),
            AppliedRuleVersion = new RuleVersion("tax-2026-10"),
            IsTaxLine = false,
            ParentLineNo = null,
            ItemDescription = "事務用品",
            BookOnlyDeduction = "public_transport",
            EvidenceRef = "DOC-1",
        };

        await server.EntryStore.ReplaceLinesAsync(id, [line]);

        Assert.Equal(line, Assert.Single((await server.EntryStore.LoadAsync(id)).Lines));
    }

    [Fact]
    public async Task 消費税行も親行の番号ごと読み戻せる()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        var body = new JournalLine
        {
            LineNo = 1,
            DebitCredit = DebitCredit.Debit,
            AccountId = server.AccountOf("6110"),
            DepartmentId = server.DepartmentOf("20"),
            Amount = Yen.From(10_000),
            TaxCategoryId = server.TaxCategoryOf("TP"),
        };
        var tax = body with
        {
            LineNo = 2,
            AccountId = server.AccountOf("1540"),
            Amount = Yen.From(1_000),
            IsTaxLine = true,
            ParentLineNo = 1,
        };

        await server.EntryStore.ReplaceLinesAsync(id, [body, tax]);

        Assert.Equal([body, tax], (await server.EntryStore.LoadAsync(id)).Lines);
    }

    [Fact]
    public async Task 課税仕入れの時点は_CLB_が書く形で保存する()
    {
        // 時刻なしで書くと、その行だけが日付の範囲検索から落ちる（qa/01 A-04）。
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        await server.EntryStore.ReplaceLinesAsync(id,
        [
            new JournalLine
            {
                LineNo = 1,
                DebitCredit = DebitCredit.Debit,
                AccountId = server.AccountOf("1100"),
                Amount = Yen.From(100),
                TaxCategoryId = server.TaxCategoryOf("OUT"),
                TaxPoint = new DateOnly(2026, 10, 1),
            },
        ]);

        Assert.Equal(
            "2026-10-01 00:00:00",
            server.Scalar<string>($"select tax_point from journal_lines where journal_entry_id = {id.Value}"));
    }

    [Fact]
    public async Task 計上済みの明細は入れ替えられない()
    {
        using var server = new AccountingServer();
        var id = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 100), ("credit", "2200", 100));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.EntryStore.ReplaceLinesAsync(id, []));

        Assert.Contains("下書きではない", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, server.Scalar<long>($"select count(*) from journal_lines where journal_entry_id = {id.Value}"));
    }

    [Fact]
    public async Task 計上済みの伝票には取消の内容を書き込めない()
    {
        using var server = new AccountingServer();
        var id = server.InsertPosted(1, "原本", "2026-08-24", ("debit", "1100", 100), ("credit", "2200", 100));
        var reversal = (await server.EntryStore.LoadAsync(id)) with { Description = "書き換え" };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.EntryStore.OverwriteReversalHeaderAsync(id, reversal));

        Assert.Contains("下書きではない", error.Message, StringComparison.Ordinal);
        Assert.Equal("原本", server.Scalar<string>($"select description from journal_entries where id = {id.Value}"));
    }

    /// <summary>
    /// サーバが自分から書く下書きが、全項目そのまま往復すること。
    /// </summary>
    /// <remarks>
    /// <b>NULL のままの列は「書けている」ことを何も証明しない</b>（R3 で明細の 17 列が
    /// 全テスト NULL だった実例がある）。任意項目を埋めた形と空の形の両方を通す。
    /// </remarks>
    [Fact]
    public async Task 下書きは全項目が往復する()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner();
        var original = server.InsertPosted(1, null, "2026-05-20", ("debit", "1100", 10), ("credit", "2200", 10));

        var draft = new JournalEntry
        {
            FiscalYearId = AccountingServer.FiscalYear,
            TransactionDate = new DateOnly(2026, 5, 20),
            PostingDate = new DateOnly(2026, 8, 24),
            Status = EntryStatus.Draft,
            EntryType = EntryType.Correction,
            OriginalEntryId = original,
            Description = "全項目を埋めた下書き",
            PartnerId = new PartnerId(partner),
            SourceComponent = "expense",
            SourceDocumentId = "EXP-001",
            IdempotencyKey = "expense/EXP-001",
            EnteredAt = AccountingServer.Now,
            Lines = [Line(server, 1, DebitCredit.Debit, "1100", 700), Line(server, 2, DebitCredit.Credit, "2200", 700)],
        };

        var loaded = await server.EntryStore.LoadAsync(await server.EntryStore.InsertDraftAsync(draft));

        Assert.Equal(draft with { Id = loaded.Id, Lines = loaded.Lines }, loaded with { Lines = loaded.Lines });
        Assert.Equal(draft.Lines, loaded.Lines);
    }

    [Fact]
    public async Task 任意項目が空の下書きも書ける()
    {
        // 取消・訂正でない下書き（原仕訳も取引先も外部投入の印も無い形）。
        using var server = new AccountingServer();
        var draft = new JournalEntry
        {
            FiscalYearId = AccountingServer.FiscalYear,
            TransactionDate = new DateOnly(2026, 8, 24),
            PostingDate = new DateOnly(2026, 8, 24),
            Status = EntryStatus.Draft,
            EntryType = EntryType.Normal,
            EnteredAt = AccountingServer.Now,
            Lines = [Line(server, 1, DebitCredit.Debit, "1100", 1)],
        };

        var loaded = await server.EntryStore.LoadAsync(await server.EntryStore.InsertDraftAsync(draft));

        Assert.Null(loaded.OriginalEntryId);
        Assert.Null(loaded.PartnerId);
        Assert.Null(loaded.Description);
        Assert.Null(loaded.SourceComponent);
        Assert.Null(loaded.SourceDocumentId);
        Assert.Null(loaded.IdempotencyKey);
        Assert.Equal(EntryStatus.Draft, loaded.Status);
    }

    private static JournalLine Line(
        AccountingServer server, int lineNo, DebitCredit side, string accountCode, long amount)
        => new()
        {
            LineNo = lineNo,
            DebitCredit = side,
            AccountId = server.AccountOf(accountCode),
            Amount = Yen.From(amount),
            TaxCategoryId = server.TaxCategoryOf("OUT"),
        };

    [Fact]
    public async Task 存在しない仕訳は見つからない()
    {
        using var server = new AccountingServer();

        Assert.Null(await server.EntryStore.FindAsync(new JournalEntryId(999)));
        Assert.Null(await server.EntryStore.FindEntryTypeAsync(new JournalEntryId(999)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.EntryStore.LoadAsync(new JournalEntryId(999)));
    }

    [Fact]
    public async Task 種別だけを読める()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 10), ("credit", "2200", 10));

        Assert.Equal(EntryType.Normal, await server.EntryStore.FindEntryTypeAsync(original));
        Assert.Equal(
            EntryType.Correction,
            await server.EntryStore.FindEntryTypeAsync(server.InsertCorrectionDraft(original)));
    }
}
