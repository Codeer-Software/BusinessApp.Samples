namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Journals;
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
        server.InsertLine(id, 2, "credit", "2100", 1000);

        var entry = await server.EntryStore.LoadAsync(id);

        Assert.Equal(id, entry.Id);
        Assert.Equal(AccountingServer.FiscalYear, entry.FiscalYearId);
        Assert.Equal(new DateOnly(2026, 8, 20), entry.TransactionDate);
        Assert.Equal(new DateOnly(2026, 8, 24), entry.PostingDate);
        Assert.Equal(EntryStatus.Draft, entry.Status);
        Assert.Equal(EntryType.Normal, entry.EntryType);
        // JST 固定で読む（AccountingTimeZone）。マシンのタイムゾーンに依存させない。
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
        var id = server.InsertDraft();

        var entry = await server.EntryStore.LoadAsync(id);

        Assert.Null(entry.EntryNo);
        Assert.Null(entry.PostedAt);
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
    public async Task 計上の印を付けると番号と計上日時と版が入る()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        await server.EntryStore.MarkPostedAsync(id, new EntryNumber(AccountingServer.FiscalYear, 7), AccountingServer.Now);

        var entry = await server.EntryStore.LoadAsync(id);
        Assert.Equal(EntryStatus.Posted, entry.Status);
        Assert.Equal(7, entry.EntryNo);
        Assert.Equal(AccountingServer.Now, entry.PostedAt);

        // 版は CLB が進めるもの。ここで進めると 1 回の保存で 2 つ進み、画面が古い版を握る。
        Assert.Equal(0, server.Scalar<long>($"select optimistic_locking from journal_entries where id = {id.Value}"));
    }

    [Fact]
    public async Task 計上済みには二度と印を付けられない()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        await server.EntryStore.MarkPostedAsync(id, new EntryNumber(AccountingServer.FiscalYear, 1), AccountingServer.Now);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.EntryStore.MarkPostedAsync(id, new EntryNumber(AccountingServer.FiscalYear, 2), AccountingServer.Now));

        Assert.Contains("下書きではない", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.Scalar<long>($"select entry_no from journal_entries where id = {id.Value}"));
    }
}
