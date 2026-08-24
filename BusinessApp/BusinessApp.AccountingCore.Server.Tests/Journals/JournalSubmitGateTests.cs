namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 保存時の関門。
/// </summary>
/// <remarks>
/// <b>ここが会計の最後の砦である。</b> 検証を通さずに <c>posted</c> になる道が
/// 1 つでもあれば ADR-0004 が崩れるので、抜け道になりうる形を重点的に置く。
/// 保存そのものは <see cref="AccountingServer.Saving"/> が
/// 「関門が書き換えた内容のとおりに書く」ので、書き換え漏れは DDL に弾かれる。
/// </remarks>
public class JournalSubmitGateTests
{
    private const string TemporaryId = "@temporary:0f0a";

    private static readonly (string DebitCredit, string AccountCode, long Amount)[] Balanced =
        [("debit", "1100", 1000), ("credit", "2100", 1000)];

    [Fact]
    public async Task 計上として送られた伝票は_番号と計上日時が付いて計上済みになる()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "posted");

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        var posted = await server.EntryStore.LoadAsync(new JournalEntryId(1));
        Assert.Equal(EntryStatus.Posted, posted.Status);
        Assert.Equal(1, posted.EntryNo);
        Assert.Equal(AccountingServer.Now, posted.PostedAt);
        Assert.Equal(2, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 入力年月日はシステムが打ち_保存された値がそのまま読み戻せる()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "posted");

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        // 「打った値」ではなく「DB に書かれて読み戻した値」を見る。
        // 書く経路と読む経路の解釈がずれていれば、ここでずれる（優良な電子帳簿 規則 5 ⑤一イ(2)）。
        Assert.Equal(AccountingServer.Now, (await server.EntryStore.LoadAsync(new JournalEntryId(1))).EnteredAt);
    }

    [Fact]
    public async Task 更新で送られてきた入力年月日は捨てる()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry("1");
        entry.Fields["EnteredAt"] = new DateTimeFieldData { Value = new DateTime(2020, 1, 1) };

        await server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved);

        // 差分に残っていると、DB のトリガ（入力年月日は変更できない）に正常系で当たる。
        Assert.False(entry.Fields.ContainsKey("EnteredAt"));
    }

    [Fact]
    public async Task 下書き保存も状態を送らない保存も計上ではない()
    {
        using var server = new AccountingServer();
        var draft = SubmitData.Entry(TemporaryId, status: "draft");

        await server.SubmitAsync(
            [SubmitData.Adding(draft), SubmitData.Updating(SubmitData.Entry("1"))],
            server.Saving(draft, Balanced));

        var saved = await server.EntryStore.LoadAsync(new JournalEntryId(1));
        Assert.Equal(EntryStatus.Draft, saved.Status);
        Assert.Null(saved.EntryNo);
        Assert.Equal(1, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 明細だけの保存を伝票と見間違えない()
    {
        using var server = new AccountingServer();

        // 明細は伝票と同じ Add / Update に混ざって届く（qa/01 F-11）。名前で見分けられなければ、
        // 明細を伝票として計上しようとして壊れる。
        await server.SubmitAsync(
            [SubmitData.Adding(SubmitData.Line(1), SubmitData.Line(2)), SubmitData.Updating(SubmitData.Line(3))],
            NothingSaved);

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
        Assert.Equal(1, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 伝票番号は会計年度の中で続き番号になる()
    {
        using var server = new AccountingServer();

        var numbers = new List<int?>();
        for (var i = 0; i < 3; i++)
        {
            var id = server.InsertDraft();
            server.InsertLine(id, 1, "debit", "1100", 100);
            server.InsertLine(id, 2, "credit", "2100", 100);
            await PostSavedAsync(server, id);
            numbers.Add((await server.EntryStore.LoadAsync(id)).EntryNo);
        }

        Assert.Equal([1, 2, 3], numbers);
    }

    [Fact]
    public async Task 会計年度が変われば伝票番号は_1_番から採り直す()
    {
        using var server = new AccountingServer();
        var next = server.InsertFiscalYear("FY19", "2027-04-01", "2028-03-31");

        var first = server.InsertDraft();
        server.InsertLine(first, 1, "debit", "1100", 100);
        server.InsertLine(first, 2, "credit", "2100", 100);
        await PostSavedAsync(server, first);

        var second = server.InsertDraft(
            transactionDate: "2027-04-01", postingDate: "2027-04-01", fiscalYearId: next);
        server.InsertLine(second, 1, "debit", "1100", 200);
        server.InsertLine(second, 2, "credit", "2100", 200);
        await PostSavedAsync(server, second);

        // I-17。年度をまたいでも通し番号にすると、年度ごとの一連番号ではなくなる。
        Assert.Equal(1, (await server.EntryStore.LoadAsync(first)).EntryNo);
        Assert.Equal(1, (await server.EntryStore.LoadAsync(second)).EntryNo);
        Assert.Equal(2, server.Scalar<long>(
            $"select next_entry_no from journal_entry_sequences where fiscal_year_id = {AccountingServer.FiscalYear.Value}"));
        Assert.Equal(2, server.Scalar<long>(
            $"select next_entry_no from journal_entry_sequences where fiscal_year_id = {next.Value}"));
    }

    [Fact]
    public async Task 計上が弾かれたら伝票も明細も採番も残らない()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "posted");

        // 貸借が合っていない。ADR-0004 が最も頼っているのは、この巻き戻しである。
        await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync(
                [SubmitData.Adding(entry)],
                server.Saving(entry, [("debit", "1100", 1000), ("credit", "2100", 900)])));

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_lines"));
        Assert.Equal(1, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 違反は全件まとめて返す()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        // 独立した違反を 2 件出す。貸借不一致（伝票）と、損益科目の部門欠落（明細）。
        // 1 件だけ見せると、直しては弾かれを繰り返すことになる。
        server.InsertLine(id, 1, "debit", "6110", 1000, taxCategoryCode: "TP");
        server.InsertLine(id, 2, "credit", "2100", 900);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostSavedAsync(server, id));

        var codes = error.Violations.Select(v => v.Code).ToList();
        Assert.Contains(JournalViolationCodes.Unbalanced, codes);
        Assert.Contains(JournalViolationCodes.DepartmentMissing, codes);
        Assert.Contains("1 行目", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 仮_ID_は保存結果の対応表で本物に読み替える()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "posted");

        // Saving は仮 ID → 実 ID の対応表を返す。読み替えられなければ計上できない。
        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        Assert.Equal(EntryStatus.Posted, (await server.EntryStore.LoadAsync(new JournalEntryId(1))).Status);
    }

    [Fact]
    public async Task 読み替えられない_ID_は止める()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "posted");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync([SubmitData.Adding(entry)], NothingSaved));

        Assert.Contains(TemporaryId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 識別子が差分に載っていない伝票は計上しない()
    {
        using var server = new AccountingServer();
        var entry = new ModuleData { Name = JournalSubmitGate.EntryModuleName };
        entry.Fields["Status"] = new SelectFieldData { Value = "posted" };

        // 黙って読み飛ばすと「計上したつもりの下書き」が残る。止めて巻き戻す。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync([SubmitData.Adding(entry)], NothingSaved));
    }

    [Fact]
    public async Task 同じ仮_ID_に本物の_ID_が二つ対応していたら止める()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "posted");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync(
                [SubmitData.Adding(entry)],
                () => Task.FromResult(new List<ModuleSubmitResult>
                {
                    SubmitData.Result((TemporaryId, "1")),
                    SubmitData.Result((TemporaryId, "2")),
                })));

        // 先勝ちで捨てると、片方が黙って別の伝票に化ける。
        Assert.Contains(TemporaryId, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// まだ作っていない種別は計上させない（種別のホワイトリスト）。
    /// </summary>
    /// <remarks>
    /// 期首残高・決算振替・繰越は、それぞれ固有の前提（I-11・I-12・繰越の再実行）を持つ。
    /// 素通りさせると、その前提を満たさない伝票が普通の仕訳として帳簿に載る。
    /// </remarks>
    [Theory]
    [InlineData("opening")]
    [InlineData("closing")]
    [InlineData("carryover")]
    public async Task 未実装の種別は計上できない(string entryType)
    {
        using var server = new AccountingServer();
        var draft = server.InsertDraft(entryType: entryType);
        server.InsertLine(draft, 1, "debit", "1100", 100);
        server.InsertLine(draft, 2, "credit", "2100", 100);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostSavedAsync(server, draft));

        Assert.Contains(JournalViolationCodes.EntryTypeNotSupported, error.Violations.Select(v => v.Code));
        Assert.Equal("draft", server.StatusOf(draft));
        Assert.Equal(1, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    /// <summary>保存が済んでいる下書きを、保存経路を通して計上させる。</summary>
    private static Task PostSavedAsync(AccountingServer server, JournalEntryId id)
    {
        var entry = SubmitData.Entry(server.Text(id.Value), status: "posted");
        return server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved);
    }

    /// <summary>何も書かない保存（既に DB にある行を計上するときに使う）。</summary>
    private static Task<List<ModuleSubmitResult>> NothingSaved() => Task.FromResult(new List<ModuleSubmitResult>());
}
