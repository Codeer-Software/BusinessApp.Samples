namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using System.Text.Json;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

/// <summary>
/// 「取り消す」「訂正する」の入口（<see cref="JournalAmendmentEndpoint"/>）。
/// </summary>
/// <remarks>
/// <para><b>ここは以前まるごと検査の外にあった。</b> コントローラに書かれていて、
/// JSON の項目名を 1 つ落としても <c>[Authorize]</c> を消しても全テストが緑だった
/// （qa/02 R4-03）。会計コア側へ移したので、カバレッジとミューテーションのゲートに載る。</para>
/// <para><b>いちばん大事なのは「取消だけが残らない」ことである</b>（qa/02 R4-05）。
/// 訂正は「取消を計上する」「再計上の下書きを作る」の 2 つで 1 操作であり、
/// 途中で失敗したときに取消だけが確定すると、利用者は<b>直す手段を失ったまま
/// 取り消された伝票</b>を抱える。ADR-0016 が Web API（A 案）を選んだ決め手がこれである。</para>
/// </remarks>
public class JournalAmendmentEndpointTests
{
    /// <summary>取り消される側の仕訳（借方 現金 1000 / 貸方 買掛金 1000）。</summary>
    private static JournalEntryId Original(AccountingServer server)
        => server.InsertPosted(
            1, "5 月分の仕入", "2026-05-20", ("debit", "1100", 1000), ("credit", "2100", 1000));

    // --- 識別子の解釈 ---

    /// <summary>
    /// 識別子が数値として読めなければ、業務のことばで差し戻す。
    /// </summary>
    /// <remarks>
    /// <b>文字列で受ける</b>のは、CLB のスクリプトが値を動的に扱うからである。
    /// 数値として送らせると「型が違うから 400」という、利用者に何も伝えない失敗になる。
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData("9999999999999999999999")]
    public async Task 識別子が読めなければ差し戻す(string? originalEntryId)
    {
        using var server = new AccountingServer();
        Original(server);

        var result = await server.Amendment.ReverseAsync(originalEntryId);

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.Equal("対象の伝票が指定されていません。", result.Message);
        Assert.Equal(
            [JournalViolationCodes.AmendmentTargetNotFound],
            result.Violations.Select(v => v.Code));

        // **何も書いていない。** 読めない識別子で伝票が増えては困る。
        Assert.Equal(1, server.Scalar<long>("select count(*) from journal_entries"));
    }

    [Fact]
    public async Task 実在しない伝票は業務のことばで差し戻す()
    {
        using var server = new AccountingServer();
        Original(server);

        var result = await server.Amendment.ReverseAsync("9999");

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.Equal(
            [JournalViolationCodes.AmendmentTargetNotFound],
            result.Violations.Select(v => v.Code));
        Assert.Equal("対象の伝票が見つかりません。", result.Violations[0].Message);

        // 文言は違反を並べる形（JournalPostingRejectedException）を通って組み立てられる。
        Assert.Contains("対象の伝票が見つかりません。", result.Message, StringComparison.Ordinal);
    }

    // --- できること ---

    [Fact]
    public async Task できることを返す()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var result = await server.Amendment.AvailabilityAsync(server.Text(original.Value));

        // **成否ではないので status は ok。** 内容は 2 つの真偽値で表す。
        Assert.Equal(AmendResult.Succeeded, result.Status);
        Assert.True(result.CanReverse);
        Assert.True(result.CanCorrect);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal(0, result.OpenEntryId);
    }

    [Fact]
    public async Task 取り消し済みならできないと返す()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.Amendment.ReverseAsync(server.Text(original.Value));

        var result = await server.Amendment.AvailabilityAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, result.Status);
        Assert.False(result.CanReverse);
        Assert.NotEqual(string.Empty, result.Message);
    }

    // --- 取り消す ---

    [Fact]
    public async Task 取り消すと反対仕訳の識別子を返す()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var result = await server.Amendment.ReverseAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, result.Status);
        Assert.Empty(result.Violations);

        // 取消では**開く伝票と計上した取消が同じ**（訂正と違って下書きを作らない）。
        Assert.Equal(result.ReversalId, result.OpenEntryId);
        Assert.Equal(1, server.CountAmendments(original, "reversal"));
        Assert.Equal("posted", server.StatusOf(new JournalEntryId(result.ReversalId)));
    }

    /// <summary>
    /// 業務として差し戻したときは<b>巻き戻っている</b>。
    /// </summary>
    /// <remarks>
    /// 差し戻しを戻り値で表すからといって、途中まで書いたものが残ってよいわけではない。
    /// </remarks>
    [Fact]
    public async Task 二重の取消は差し戻して何も残さない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.Amendment.ReverseAsync(server.Text(original.Value));
        var before = server.Scalar<long>("select count(*) from journal_entries");

        var result = await server.Amendment.ReverseAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.NotEmpty(result.Violations);
        Assert.Equal(before, server.Scalar<long>("select count(*) from journal_entries"));
    }

    // --- 訂正する ---

    [Fact]
    public async Task 訂正すると開くのは再計上の下書きである()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var result = await server.Amendment.CorrectAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, result.Status);

        // **開くのは下書きのほう。** ここを取り違えると、利用者は計上済みの取消を開いて
        // 「直せない」と詰まる（ADR-0015）。
        Assert.NotEqual(result.ReversalId, result.OpenEntryId);
        Assert.Equal("posted", server.StatusOf(new JournalEntryId(result.ReversalId)));
        Assert.Equal("draft", server.StatusOf(new JournalEntryId(result.OpenEntryId)));
        Assert.Equal(1, server.CountAmendments(original, "correction", "draft"));
    }

    /// <summary>
    /// 途中で失敗したら<b>取消だけが残らない</b>。
    /// </summary>
    /// <remarks>
    /// <para><b>この 1 本が、ADR-0016 が Web API（A 案）を選んだ決め手そのものである。</b>
    /// 訂正は 2 つの書き込みで 1 操作なので、1 回の呼び出しの中で 1 つのトランザクションを
    /// 張れる形でなければならない。スクリプトから 2 回保存する案（B 案）では、
    /// 取消だけが計上されて再計上の下書きが無い状態を巻き戻せない。</para>
    /// <para>そうなると利用者から見えるのは<b>「取り消された伝票」だけ</b>で、
    /// 単なる取消と区別が付かない。直したかった内容は失われる。</para>
    /// <para>再計上の下書きを書く直前（＝2 回目の伝票の挿入）で落とす。</para>
    /// </remarks>
    [Fact]
    public async Task 訂正が途中で失敗したら取消だけが残らない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        var before = server.Scalar<long>("select count(*) from journal_entries");

        var inserts = 0;
        server.FailBeforeStatement = sql =>
            sql.Contains("insert into journal_entries", StringComparison.OrdinalIgnoreCase) && ++inserts == 2
                ? new InvalidOperationException("再計上の下書きを書く直前で落とす")
                : null;

        // 想定外の失敗なので、業務の差し戻しではなくそのまま投げ直す（ADR-0016）。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Amendment.CorrectAsync(server.Text(original.Value)));

        server.FailBeforeStatement = null;

        // **取消が残っていない。** ここが 1 件でも残ると、この機能は成立していない。
        Assert.Equal(0, server.CountAmendments(original, "reversal"));
        Assert.Equal(0, server.CountAmendments(original, "correction", "draft"));
        Assert.Equal(before, server.Scalar<long>("select count(*) from journal_entries"));

        // 原仕訳は無傷で、あらためて訂正できる。
        Assert.Equal("posted", server.StatusOf(original));
        Assert.Equal(AmendResult.Succeeded, (await server.Amendment.CorrectAsync(server.Text(original.Value))).Status);
    }

    // --- 画面との約束（JSON の項目名） ---

    /// <summary>
    /// 応答の項目名を固定する。
    /// </summary>
    /// <remarks>
    /// <b>スクリプトはキーを文字列で引く</b>ので、名前が変わってもコンパイルは通り、
    /// 画面が黙って値を読めなくなる（qa/01 K-03）。以前はこの層に検査が無く、
    /// <b>項目を 1 つ落としても全テストが緑だった</b>（qa/02 R4-03）。
    /// </remarks>
    [Fact]
    public void 応答の項目名は画面との約束である()
    {
        var json = JsonSerializer.Serialize(
            AmendResult.Rejected("だめ", [new AmendViolation("E-01", "理由", 2)]));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // **名前を並び順ごと固定する。** スクリプトはキーを文字列で引くので、
        // 1 つ落ちても改名されてもコンパイルは通り、画面が黙って値を読めなくなる。
        Assert.Equal(
            ["status", "openEntryId", "reversalId", "message", "violations", "canReverse", "canCorrect"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["code", "message", "lineNo"],
            root.GetProperty("violations")[0].EnumerateObject().Select(property => property.Name));

        Assert.Equal(AmendResult.RejectedStatus, root.GetProperty("status").GetString());
        Assert.Equal("E-01", root.GetProperty("violations")[0].GetProperty("code").GetString());
        Assert.Equal(2, root.GetProperty("violations")[0].GetProperty("lineNo").GetInt32());
    }

    [Fact]
    public void 要求の項目名も画面との約束である()
    {
        var request = JsonSerializer.Deserialize<AmendRequest>("""{"originalEntryId":"12"}""");

        Assert.Equal("12", request!.OriginalEntryId);
    }
}
