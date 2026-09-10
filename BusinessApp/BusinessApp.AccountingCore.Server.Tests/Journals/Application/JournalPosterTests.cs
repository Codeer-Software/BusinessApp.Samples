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
    /// <b>無効にした取引先は、新たな計上に使えない</b>（docs/10 §6-2）。
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
        Assert.Contains(
            "取引先「取引をやめた先」は無効なので、新しい計上には使えません。別の取引先を選ぶか、取引先の画面で有効に戻してください。",
            thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", server.Scalar<string>($"select status from journal_entries where id = {id.Value}"));
    }

    /// <summary>明細の取引先も本番の配線で見る（伝票の取引先だけ読んで明細を読み忘れる形。qa/03 L-14）。行番号つきで断る。</summary>
    [Fact]
    public async Task 無効な取引先を持つ明細は計上できない()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner("P900", "取引をやめた先");
        server.Execute($"update partners set is_active = 0 where id = {partner}");
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "1110", 1000);
        server.Execute($"update journal_lines set partner_id = {partner} where journal_entry_id = {id.Value} and line_no = 2");

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            async () => await server.Poster.PostAsync(await server.EntryStore.LoadAsync(id), await server.MasterLoader.LoadAsync()));

        var violation = Assert.Single(thrown.Violations, v => v.Code == JournalViolationCodes.PartnerInactive);
        Assert.Equal(2, violation.LineNo);
        Assert.Contains("行 2: 取引先「取引をやめた先」は無効", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>マスタに無い取引先</b>は本番の配線で <c>E-PARTNER-UNKNOWN</c> になる（「読み忘れ」と「不在」は区別しないので、不在の側も踏む）。
    /// 外部キーが効いていると作れない状態なので、この検体だけ外す（DDL のトリガが想定する「外部キーを切った経路」）。
    /// </summary>
    [Fact]
    public async Task マスタに無い取引先を持つ伝票は計上できない()
    {
        using var server = new AccountingServer();
        server.InsertPartner();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "1110", 1000);
        server.Execute("pragma foreign_keys = off");
        server.Execute($"update journal_entries set partner_id = 999 where id = {id.Value}");

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            async () => await server.Poster.PostAsync(await server.EntryStore.LoadAsync(id), await server.MasterLoader.LoadAsync()));

        Assert.Equal([JournalViolationCodes.PartnerUnknown], thrown.Violations.Select(v => v.Code));
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
