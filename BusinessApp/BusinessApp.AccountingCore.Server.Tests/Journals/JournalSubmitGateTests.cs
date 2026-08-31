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
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        var posted = await server.EntryStore.LoadAsync(new JournalEntryId(1));
        Assert.Equal(EntryStatus.Posted, posted.Status);
        Assert.Equal(1, posted.EntryNo);
        Assert.Equal(AccountingServer.Now, posted.PostedAt);
        Assert.Equal(AccountingServer.CurrentUser, posted.PostedBy);
        Assert.Equal(2, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    /// <summary>
    /// 認証の識別子が<b>空</b>のとき（認証の無い経路）、計上した人は
    /// <b>偽の値で埋めず NULL のまま</b>にする（qa/02 R2-05。列は NULL 可）。
    /// </summary>
    [Fact]
    public async Task 認証の識別子が空なら計上した人はNULLになる()
    {
        using var server = new AccountingServer();
        server.CurrentUserId = string.Empty;
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        var posted = await server.EntryStore.LoadAsync(new JournalEntryId(1));
        Assert.Equal(EntryStatus.Posted, posted.Status);
        Assert.Null(posted.PostedBy);
    }

    /// <summary>
    /// 識別子が<b>空でないのに正の整数として読めない</b>のは認証の設定の壊れ。
    /// 黙って NULL で計上すると、計上済みは不変（I-05）なので記帳者の記録が永久に失われる。
    /// 音を立てて止め、保存ごと巻き戻す。
    /// </summary>
    [Theory]
    [InlineData("admin")]
    [InlineData("-1")]
    [InlineData("0")]
    public async Task 認証の識別子が壊れていたら計上は止まり保存ごと巻き戻る(string brokenUserId)
    {
        using var server = new AccountingServer();
        server.CurrentUserId = brokenUserId;
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced)));

        Assert.Equal(0L, server.Scalar<long>("select count(*) from journal_entries"));
    }

    [Fact]
    public async Task 入力年月日はシステムが打ち_保存された値がそのまま読み戻せる()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

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
        var draft = SubmitData.NewEntry(TemporaryId, status: "draft");

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
        //
        // **明細に「計上済み」を持たせる。** 持たせないと、状態を見るところを素通りするので
        // 「名前で見分けている」ことを何も表明しないテストになる。
        var posted = SubmitData.Line(1);
        posted.Fields["Status"] = new SelectFieldData { Value = "posted" };

        await server.SubmitAsync(
            [SubmitData.Adding(posted, SubmitData.Line(2)), SubmitData.Updating(SubmitData.Line(3))],
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
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

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
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        // Saving は仮 ID → 実 ID の対応表を返す。読み替えられなければ計上できない。
        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        Assert.Equal(EntryStatus.Posted, (await server.EntryStore.LoadAsync(new JournalEntryId(1))).Status);
    }

    [Fact]
    public async Task 読み替えられない_ID_は止める()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync([SubmitData.Adding(entry)], NothingSaved));

        Assert.Contains(TemporaryId, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 識別子が差分に載っていない伝票は計上しない()
    {
        using var server = new AccountingServer();

        // 必須項目は揃えたうえで識別子だけを落とす。揃えないと必須項目の関門が先に止めてしまい、
        // **この検査が見たい経路（ID の解決）まで届かない**。
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");
        entry.Fields.Remove("Id");

        // 黙って読み飛ばすと「計上したつもりの下書き」が残る。止めて巻き戻す。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync([SubmitData.Adding(entry)], NothingSaved));
    }

    [Fact]
    public async Task 同じ仮_ID_に本物の_ID_が二つ対応していたら止める()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync(
                [SubmitData.Adding(entry)],
                () => Task.FromResult(new List<ModuleSubmitResult>
                {
                    SubmitData.Result(TemporaryId, "1"),
                    SubmitData.Result(TemporaryId, "2"),
                })));

        // 先勝ちで捨てると、片方が黙って別の伝票に化ける。
        Assert.Contains(TemporaryId, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 保存が失敗していたら、計上へ進まない。
    /// </summary>
    /// <remarks>
    /// <para><b>CLB は保存の失敗を例外ではなく <c>ExceptionMessage</c> に詰めて返す。</b>
    /// 見ずに先へ進むと、まだ書けていない伝票を計上しようとして
    /// 「仮 ID を解決できない」という二次的な内部エラーに化け、<b>本当の理由が利用者に届かない</b>。</para>
    /// <para>実機で踏んだ形である——明細の勘定科目を空のまま計上すると、
    /// 保存が NOT NULL 違反で失敗しているのに、画面には ID の話が出ていた
    /// （2026-08-26。[qa/03](../../../docs/qa/03_テストで漏らした実例.md) L-10）。</para>
    /// </remarks>
    [Fact]
    public async Task 保存が失敗していたら計上へ進まない()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        var results = await server.SubmitAsync(
            [SubmitData.Adding(entry)],
            () => Task.FromResult(new List<ModuleSubmitResult>
            {
                SubmitData.Failure("NOT NULL constraint failed: journal_lines.account_id"),
            }));

        // **保存の失敗は握りつぶさず返す。** CLB 本来の経路が理由を報告する。
        // ただし利用者に見せるのは DB の言葉ではない——いちばん外の
        // AccountingSubmitPipeline が利用者の語に差し替える（qa/01 F-16）。
        Assert.Equal([SaveFailureMessage.Text], results.Select(r => r.ExceptionMessage));

        // 計上へ進んでいない（進むと ID を解決できずに InvalidOperationException になる）。
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries where status = 'posted'"));
    }

    /// <summary>
    /// 必須項目の無い明細は<b>保存に届かない</b>（qa/03 L-16。L-14 の処方を仕訳明細へ当てたもの）。
    /// </summary>
    /// <remarks>
    /// <para>実機で踏んだ形そのものである——明細を 1 行足して何も入れずに「計上する」を押すと、
    /// <c>SQLite Error 19: 'NOT NULL constraint failed: journal_lines.account_id'</c> が
    /// そのままトーストに出た（2026-08-28。開発者が発見）。</para>
    /// <para><b>見出しは押されたボタンで決まる。</b> ここは「計上する」を押した保存なので
    /// 「計上できません」。同じ違反を下書き保存で起こしたときは「保存できません」になる（次のテスト）。</para>
    /// </remarks>
    [Fact]
    public async Task 必須項目の無い明細は保存に届かない()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");
        var saved = false;

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync(
                [SubmitData.Adding(entry, SubmitData.LineWithout(1, "Account"))],
                () =>
                {
                    saved = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(saved);
        Assert.Equal("計上できません。①1 行目: 勘定科目を選んでください。", error.Message);
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_lines"));
    }

    /// <summary>
    /// 下書き保存でも同じ関門が効く。<b>DB が拒むかどうかに、伝票の状態は関係ない。</b>
    /// </summary>
    [Fact]
    public async Task 下書き保存でも必須項目の無い明細は止まる()
    {
        using var server = new AccountingServer();
        var draft = SubmitData.NewEntry(TemporaryId, status: "draft");
        var saved = false;

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync(
                [SubmitData.Adding(draft, SubmitData.LineWithout(1, "Amount"))],
                () =>
                {
                    saved = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(saved);
        Assert.Equal("保存できません。①1 行目: 金額を入力してください。", error.Message);
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
    }

    /// <summary>
    /// <b>保存前の関門どうしの間でも「1 件で止めない」を守る。</b>
    /// </summary>
    /// <remarks>
    /// 種別の検査と必須項目の検査を別々に投げると、利用者は種別を直して保存し直してから
    /// 明細の差し戻しを受ける——例外が全件を並べる理由（直しては弾かれを繰り返させない）を、
    /// 関門どうしの間で破ることになる。
    /// </remarks>
    [Fact]
    public async Task 種別の違反と必須項目の違反は一度にまとめて返す()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 100), ("credit", "2100", 100));
        var correction = server.InsertCorrectionDraft(original);

        var entry = SubmitData.Entry(server.Text(correction.Value), status: "draft");
        entry.Fields["EntryType"] = new SelectFieldData { Value = "normal" };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync(
                [SubmitData.Updating(entry), SubmitData.Adding(SubmitData.LineWithout(2, "Account"))],
                NothingSaved));

        Assert.Equal(
            [JournalViolationCodes.RequiredValueMissing, JournalViolationCodes.EntryTypeImmutable],
            error.Violations.Select(v => v.Code));
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

    /// <summary>
    /// 既にある伝票の種別は変えられない。
    /// </summary>
    /// <remarks>
    /// **ここが開いていると取引が帳簿に 2 回載る。** 訂正の下書きを「通常」に変えて計上すると、
    /// 種別ごとの関門を通らないうえ、二重訂正の検出は `correction` の行しか数えないので、
    /// 同じ原仕訳にもう 1 本訂正を計上できる（2026-08-25 の自己レビューで発見）。
    /// </remarks>
    [Fact]
    public async Task 既にある伝票の種別は変えられない()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 100), ("credit", "2100", 100));
        var correction = server.InsertCorrectionDraft(original);

        var entry = SubmitData.Entry(server.Text(correction.Value), status: "draft");
        entry.Fields["EntryType"] = new SelectFieldData { Value = "normal" };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved));

        Assert.Contains(JournalViolationCodes.EntryTypeImmutable, error.Violations.Select(v => v.Code));

        // **「下書きを作り直してください」とは言わない。** 訂正の下書きでそう言われても、
        // 元の伝票は既に取り消してあるので、同じものをもう一度は作れない（行き止まりになる）。
        Assert.Equal(
            "伝票の種別は、保存したあとは変更できません（「訂正」のままです）。"
            + "別の種別で起票するときは、新しい振替伝票を作成してください。",
            error.Violations.Single(v => v.Code == JournalViolationCodes.EntryTypeImmutable).Message);
        Assert.Equal("correction", server.Scalar<string>(
            $"select entry_type from journal_entries where id = {correction.Value}"));
    }

    [Fact]
    public async Task 同じ種別を送り直すのは通る()
    {
        // 画面は変更した項目だけを送ってくるとは限らない（qa/01 F-12）。
        // 「同じ値が来た」を変更と誤判定すると、普通の保存が止まる。
        using var server = new AccountingServer();
        var draft = server.InsertDraft();

        var entry = SubmitData.Entry(server.Text(draft.Value), status: "draft");
        entry.Fields["EntryType"] = new SelectFieldData { Value = "normal" };

        await server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved);

        Assert.Equal("draft", server.StatusOf(draft));
    }

    [Fact]
    public async Task 仮_ID_のまま種別を送ってきても止めない()
    {
        // 新規は保存が済むまで本物の ID を持たない（qa/01 C-08）。
        // ここで止めると、種別を選んで新規保存する普通の操作ができなくなる。
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(TemporaryId, status: "draft");
        entry.Fields["EntryType"] = new SelectFieldData { Value = "normal" };

        await server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved);

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
    }

    [Fact]
    public async Task 存在しない伝票の種別は判定しない()
    {
        // 種別の検査は「変えたかどうか」だけを見る。**伝票があるかどうかは保存側の仕事**で、
        // ここで落とすと「存在しない」ことが種別の違反として届く（コードの意味がずれる）。
        using var server = new AccountingServer();
        var entry = SubmitData.Entry("999", status: "draft");
        entry.Fields["EntryType"] = new SelectFieldData { Value = "reversal" };

        await server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved);

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
    }

    // --- 削除（ADR-0027 §4。一覧の削除アイコンが無くなり、詳細画面のボタンへ移した）---

    /// <summary>
    /// <b>計上済みの伝票は削除できない。</b> DDL のトリガも拒むが、そこまで行かせない
    /// （行かせると生の SQLite の例外が利用者に出る。qa/01 F-16）。
    /// </summary>
    [Fact]
    public async Task 計上済みの伝票の削除は関門が止める()
    {
        using var server = new AccountingServer();
        // **識別子と伝票番号をずらす**（既定ではどちらも 1 になり、取り違えを検出できない）。
        server.StartEntryNumbersAt(101);
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "2100", 1000);
        await PostSavedAsync(server, id);

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Deleting(server.Text(id.Value))], NothingSaved));

        Assert.StartsWith(JournalPostingRejectedException.DeletionHeadline, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("伝票番号 101", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(JournalViolationCodes.AlreadyPosted, thrown.Violations.Select(v => v.Code));
        Assert.Equal(1, server.Scalar<long>("select count(*) from journal_entries"));
    }

    /// <summary>締め済みの会計期間に属する下書きは削除できない。</summary>
    [Fact]
    public async Task 締め済みの期間の下書きの削除は関門が止める()
    {
        using var server = new AccountingServer();
        // **取引日と計上日をずらす。** 同じ日にすると、文言が計上日を出しているのか
        // 取引日を出しているのか区別できない（qa/03 L-02）。締めは計上日で見る。
        var id = server.InsertDraft(transactionDate: "2026-08-20", postingDate: "2026-08-24");
        server.Execute("update accounting_periods set status = 'closed' where start_date <= '2026-08-24 00:00:00' and '2026-08-24 00:00:00' <= end_date");

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Deleting(server.Text(id.Value))], NothingSaved));

        Assert.StartsWith(JournalPostingRejectedException.DeletionHeadline, thrown.Message, StringComparison.Ordinal);
        Assert.Contains("2026-08-24", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-20", thrown.Message, StringComparison.Ordinal);
        Assert.Contains(JournalViolationCodes.PeriodClosed, thrown.Violations.Select(v => v.Code));
    }

    /// <summary>会計年度を締めても同じ（期間だけを見ていると素通りする）。</summary>
    [Fact]
    public async Task 締め済みの年度の下書きの削除も関門が止める()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.Execute("update fiscal_years set status = 'closed'");

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Deleting(server.Text(id.Value))], NothingSaved));

        Assert.Contains(JournalViolationCodes.PeriodClosed, thrown.Violations.Select(v => v.Code));
    }

    /// <summary>
    /// 開いている期間の下書きは、<b>明細ごと消える</b>。
    /// </summary>
    /// <remarks>
    /// <b>「関門が止めなかった」ではなく「行が消えた」を表明する。</b>
    /// 明細まで見るのは、親の <c>ListField</c> が <c>DeleteTogether: true</c> で
    /// 子を一緒に消す形にしてあるからである（qa/01 C-03b）——
    /// 子が残ると外部キーで親が消せず、CLB は <c>false</c> を返して静かに終わる（C-03）。
    /// </remarks>
    [Fact]
    public async Task 開いている期間の下書きは明細ごと削除できる()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "2100", 1000);

        await server.SubmitAsync([SubmitData.Deleting(server.Text(id.Value))], server.Deleting(id));

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_lines"));
    }

    /// <summary>
    /// <b>会計期間が無い日の下書きは止めない。</b> 締めようもない期間で消せなくすると、
    /// 期間の設定を直すまで消せない下書きが残る。
    /// </summary>
    [Fact]
    public async Task 会計期間が無い日の下書きの削除は通る()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.Execute("delete from accounting_periods");

        await server.SubmitAsync([SubmitData.Deleting(server.Text(id.Value))], server.Deleting(id));

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
    }

    /// <summary>保存されていない行（仮の識別子・消えている行）は、消しても帳簿が動かない。</summary>
    [Theory]
    [InlineData(TemporaryId)]
    [InlineData("999")]
    public async Task 保存されていない伝票の削除は通る(string id)
    {
        using var server = new AccountingServer();
        var untouched = server.InsertDraft();

        await server.SubmitAsync([SubmitData.Deleting(id)], NothingSaved);

        // **関係の無い行を巻き込んでいない。** 「例外が出なかった」だけでは、
        // 削除の対象を取り違えていても通る。
        Assert.Equal(1, server.Scalar<long>("select count(*) from journal_entries"));
        Assert.Equal(untouched.Value, server.Scalar<long>("select id from journal_entries"));
    }

    /// <summary>
    /// <b>削除が無い保存では、会計年度と期間を読みに行かない。</b>
    /// いちばん多い経路（下書き保存）に無駄な往復を足さないための早期 return を固定する。
    /// </summary>
    [Fact]
    public async Task 削除が無い保存はマスタを読みに行かない()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "draft");
        var readPeriods = 0;
        server.FailBeforeStatement = sql =>
        {
            if (sql.Contains("accounting_periods", StringComparison.Ordinal))
            {
                readPeriods++;
            }

            return null;
        };

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        Assert.Equal(0, readPeriods);
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
