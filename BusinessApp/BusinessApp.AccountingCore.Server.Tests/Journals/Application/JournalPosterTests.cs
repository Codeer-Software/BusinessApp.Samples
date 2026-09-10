namespace BusinessApp.AccountingCore.Server.Tests.Journals.Application;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals.Application;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

/// <summary>
/// 計上の実行（検証 → 採番 → 状態遷移）。
/// </summary>
/// <remarks>
/// 正常系は関門と「訂正する／取り消す」の検査が通す。ここに置くのは、
/// <b>別経路から呼ばれたときの守り</b>だけである。
/// </remarks>
public class JournalPosterTests
{
    /// <summary>
    /// <b>無効にした取引先は、新たな計上に使えない</b>（docs/04 §1 の B-1「A-4 の残り」）。
    /// </summary>
    /// <remarks>
    /// 本番の配線（<c>JournalPoster.Create</c>）で、計上の直前に取引先の目録が足されることを見る——
    /// 目録を足す場所を呼び出し側に任せると、経路 1 本で忘れる（qa/03 L-14 の型）。
    /// </remarks>
    [Fact]
    public async Task 無効な取引先を持つ伝票は計上できない()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner("P900", "取引をやめた先");
        server.Execute($"update partners set is_active = 0 where id = {partner}");
        var id = server.InsertDraft();
        server.Execute($"update journal_entries set partner_id = {partner} where id = {id.Value}");
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "1110", 1000);

        var draft = await server.EntryStore.LoadAsync(id);
        var context = await server.MasterLoader.LoadAsync();

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.Poster.PostAsync(draft, context));

        Assert.Contains(thrown.Violations, v => v.Code == JournalViolationCodes.PartnerInactive);
        Assert.Contains("取引先「取引をやめた先」は無効なので、新しい計上には使えません。", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", server.Scalar<string>($"select status from journal_entries where id = {id.Value}"));
    }

    [Fact]
    public async Task 保存されていない仕訳は計上できない()
    {
        using var server = new AccountingServer();
        var poster = server.Poster;
        var draft = new JournalEntry
        {
            Id = null,
            FiscalYearId = AccountingServer.FiscalYear,
            TransactionDate = new DateOnly(2026, 8, 24),
            PostingDate = new DateOnly(2026, 8, 24),
            Status = EntryStatus.Draft,
            EntryType = EntryType.Normal,
            EnteredAt = AccountingServer.Now,
            Lines = [],
        };

        var context = await server.MasterLoader.LoadAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => poster.PostAsync(draft, context));

        Assert.Contains("保存されていない", error.Message, StringComparison.Ordinal);
    }
}
