namespace BusinessApp.AccountingCore.Server.Tests.Journals.Application;

using BusinessApp.AccountingCore.Journals;
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
