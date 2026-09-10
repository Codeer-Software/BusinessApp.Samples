namespace BusinessApp.AccountingCore.Server.Tests.Journals.Application;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Microsoft.Data.Sqlite;
using BusinessApp.AccountingCore.Server.Journals.Application;
using BusinessApp.AccountingCore.Server.Shared.Presentation;

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
        [("debit", "1100", 1000), ("credit", "2200", 1000)];

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

    /// <summary>
    /// 摘要のない伝票は計上できない（docs/10 §4-2-1）。
    /// </summary>
    /// <remarks>
    /// <b>純粋関数のテスト（<c>JournalEntryValidatorTests</c>）とは別に、DB を通した経路でも見る。</b>
    /// 保存の差分に摘要が載らなければ列は NULL のままで、
    /// <b>関門は保存の後に読み直した伝票を見る</b>ので、ここが本番と同じ形になる。
    /// </remarks>
    [Fact]
    public async Task 摘要のない伝票は計上できず_保存ごと巻き戻る()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntryWithout(TemporaryId, "Description");
        entry.Fields["Status"] = new SelectFieldData { Value = "posted" };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Adding(entry, SubmitData.Line())], server.Saving(entry, Balanced)));

        Assert.Contains("「摘要」が入っていません。", error.Message, StringComparison.Ordinal);

        // **巻き戻ることまで見る。** 差し戻したのに行が残ると、下書きが黙って増える。
        Assert.Equal(0L, server.Scalar<long>("select count(*) from journal_entries"));
    }

    [Fact]
    public async Task 入力年月日はシステムが打ち_保存された値がそのまま読み戻せる()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        // 「打った値」ではなく「DB に書かれて読み戻した値」を見る。
        // 書く経路と読む経路の解釈がずれていれば、ここでずれる（優良な電子帳簿 電帳規則 5 ⑤一イ(2)）。
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

    [Theory]
    // **デザインの型と同じ型を差し込む。** いまは Fields.Remove が型を見ないので何でも通るが、
    // 型ごとの処理に変えた日に「本番では起きない形」でだけ緑になる（qa/03 L-02 の縮退と同じ型）。
    [InlineData("PostedBy", "link")]
    [InlineData("Creator", "link")]
    [InlineData("Updater", "link")]
    [InlineData("PostedAt", "datetime")]
    [InlineData("CreatedAt", "datetime")]
    [InlineData("UpdatedAt", "datetime")]
    [InlineData("EntryNo", "number")]
    [InlineData("PartnerNameSnapshot", "text")]
    public async Task システムが決める欄は送られてきても採らない(string field, string kind)
    {
        using var server = new AccountingServer();

        // 守っているのは**画面を通らない経路**である（ADR-0004）。画面の欄は閲覧専用だが、
        // Web API を直に叩けば載せられる。**通すと記帳者・計上日時・写しを詐称できる。**
        var entry = SubmitData.Entry("1");
        entry.Fields[field] = Forged(kind);

        await server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved);

        Assert.False(entry.Fields.ContainsKey(field));
    }

    /// <summary>
    /// <b>楽観ロックは捨てない。</b> 利用者が決める値ではないが、
    /// <b>画面が持ってくるべき値</b>で、捨てると CLB の同時更新の検出が働かなくなる。
    /// </summary>
    [Fact]
    public async Task 楽観ロックの版は捨てない()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.Execute($"update journal_entries set optimistic_locking = 3 where id = {id.Value}");
        var entry = SubmitData.Entry(server.Text(id.Value));
        entry.Fields["OptimisticLocking"] = new NumberFieldData { Value = 3 };

        await server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved);

        Assert.True(entry.Fields.ContainsKey("OptimisticLocking"));
    }

    [Fact]
    public async Task 新規の保存でもシステムが決める欄は採らない()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "draft");
        entry.Fields["PostedBy"] = Forged("link");

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        Assert.False(entry.Fields.ContainsKey("PostedBy"));
    }

    private static FieldDataBase Forged(string kind) => kind switch
    {
        "link" => new LinkFieldData { Value = "999" },
        "datetime" => new DateTimeFieldData { Value = new DateTime(2020, 1, 1) },
        "number" => new NumberFieldData { Value = 999 },
        _ => new TextFieldData { Value = "999" },
    };

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
            server.InsertLine(id, 2, "credit", "2200", 100);
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
        server.InsertLine(first, 2, "credit", "2200", 100);
        await PostSavedAsync(server, first);

        var second = server.InsertDraft(
            transactionDate: "2027-04-01", postingDate: "2027-04-01", fiscalYearId: next);
        server.InsertLine(second, 1, "debit", "1100", 200);
        server.InsertLine(second, 2, "credit", "2200", 200);
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
                server.Saving(entry, [("debit", "1100", 1000), ("credit", "2200", 900)])));

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_lines"));
        Assert.Equal(1, server.Scalar<long>("select next_entry_no from journal_entry_sequences"));
    }

    [Fact]
    public async Task 取引先を要する科目の明細は関門が止める()
    {
        // **関門が DDL のトリガより先に鳴ることを、通しで表明する**（docs/10 §6-2）。
        // ここが無いと、利用者に届くのは生の `SQLite Error 19` になる——
        // この製品は同じ形で 3 回踏んでいる（qa/03 L-16・L-28・L-30）。
        using var server = new AccountingServer();
        server.Execute("update accounts set requires_partner = 1 where code = '2200'");
        // **取引先を 1 件作る**——1 件も無いと案内が「登録してから選んでください」に変わり、
        // ここで見たい本筋の文言が出ない（その分岐は JournalEntryValidatorTests が持つ）。
        server.InsertPartner();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "2200", 1000);

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(() => PostSavedAsync(server, id));

        Assert.Contains(JournalViolationCodes.PartnerRequired, thrown.Violations.Select(v => v.Code));
        Assert.Contains("勘定科目「未払金」は「取引先を要する」がオンです。伝票の「取引先」を選んでください。",
            thrown.Message, StringComparison.Ordinal);
        // **下書きのままである**（計上の巻き戻し。ADR-0004）。
        Assert.Equal("draft", server.Scalar<string>($"select status from journal_entries where id = {id.Value}"));
    }

    [Fact]
    public async Task 伝票の取引先で足りる()
    {
        // **実効値で見る**（JournalEntry.PartnerOf）。明細が空でも伝票のものが帳簿に載るので、
        // ここで止めると**帳簿には取引先が載る行を関門が拒む**ことになる。
        using var server = new AccountingServer();
        server.Execute("update accounts set requires_partner = 1 where code = '2200'");
        var partner = server.InsertPartner();
        var id = server.InsertDraft(partnerId: partner);
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "2200", 1000);

        await PostSavedAsync(server, id);

        var posted = await server.EntryStore.LoadAsync(id);
        Assert.Equal(EntryStatus.Posted, posted.Status);
        Assert.Null(posted.Lines.Single(l => l.LineNo == 2).PartnerId);
    }

    [Fact]
    public async Task 違反は全件まとめて返す()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();

        // 独立した違反を 2 件出す。貸借不一致（伝票）と、損益科目の部門欠落（明細）。
        // 1 件だけ見せると、直しては弾かれを繰り返すことになる。
        server.InsertLine(id, 1, "debit", "6110", 1000, taxCategoryCode: "TP");
        server.InsertLine(id, 2, "credit", "2200", 900);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostSavedAsync(server, id));

        var codes = error.Violations.Select(v => v.Code).ToList();
        Assert.Contains(JournalViolationCodes.Unbalanced, codes);
        Assert.Contains(JournalViolationCodes.DepartmentMissing, codes);
        Assert.Contains("行 1:", error.Message, StringComparison.Ordinal);
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

    /// <summary>開発者向けの文言（仮 ID）は利用者には定型文で、原文はログへ（ADR-0051）。</summary>
    [Fact]
    public async Task 読み替えられない_ID_は止める()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "posted");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync([SubmitData.Adding(entry)], NothingSaved));

        Assert.Equal(SaveFailureMessage.Text, error.Message);
        Assert.Contains(TemporaryId, Assert.Single(server.SaveFailureLog), StringComparison.Ordinal);
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

        // 先勝ちで捨てると、片方が黙って別の伝票に化ける。文言は開発者向けなので、利用者には定型文（ADR-0051）。
        Assert.Equal(SaveFailureMessage.Text, error.Message);
        Assert.Contains(TemporaryId, Assert.Single(server.SaveFailureLog), StringComparison.Ordinal);
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
        Assert.Equal("計上できません。行 1: 勘定科目を選んでください。", error.Message);
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
        Assert.Equal("保存できません。行 1: 金額を入力してください。", error.Message);
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
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 100), ("credit", "2200", 100));
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
        server.InsertLine(draft, 2, "credit", "2200", 100);

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
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 100), ("credit", "2200", 100));
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
        server.InsertLine(id, 2, "credit", "2200", 1000);
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
        Assert.Contains("2026/08/24", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("2026/08/20", thrown.Message, StringComparison.Ordinal);
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
        server.InsertLine(id, 2, "credit", "2200", 1000);

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

    /// <summary>
    /// 保存されていない行（仮の識別子・消えている行）は、消しても帳簿が動かない。
    /// <b>版の無い削除に限る</b>（画面を通らない経路。対照）——画面の削除は版を運んでくるので、消えていれば断る（下の「別の人が先に消した」）。
    /// </summary>
    [Theory]
    [InlineData(TemporaryId)]
    [InlineData("999")]
    [InlineData("existing")]
    public async Task 版の無い削除は_保存されていない伝票でも通る(string id)
    {
        using var server = new AccountingServer();
        var untouched = server.InsertDraft();
        if (id == "existing")
        {
            id = server.Text(untouched.Value);
        }

        await server.SubmitAsync([SubmitData.Deleting(id, version: null)], NothingSaved);

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

    // --- 空にした参照（qa/03 L-23）---

    /// <summary>
    /// <b>参照欄を空にした保存が、DB に拒まれない。</b>
    /// </summary>
    /// <remarks>
    /// <para>画面で参照欄の × を押すと <c>LinkFieldData.Value</c> は<b>空文字</b>になる。
    /// そのまま <c>INTEGER REFERENCES …</c> の列へ書くと
    /// <c>SQLite Error 19: 'FOREIGN KEY constraint failed'</c> で保存ごと落ち、
    /// 利用者には「保存できませんでした」としか出ない（2026-09-02 実測 1.3.20）。</para>
    /// <para><b>DB が実際に拒むことも同じテストで見る。</b> 関門が直したことだけを見ると、
    /// 「そもそも DB は空文字を受け取れた」との区別が付かない。</para>
    /// </remarks>
    [Fact]
    public async Task 空にした参照は無いに直してから保存へ渡す()
    {
        using var server = new AccountingServer();
        var line = SubmitData.LineWith(3, "SubAccount", new LinkFieldData { Value = string.Empty });
        var passed = new List<ModuleSubmitData>();

        await server.Gate.SubmitAsync([SubmitData.Adding(line)], () =>
        {
            passed.Add(new ModuleSubmitData());
            return Task.FromResult(new List<ModuleSubmitResult>());
        });

        Assert.Single(passed);
        Assert.Null((line.Fields["SubAccount"] as LinkFieldData)?.Value);

        // **空文字のままなら DB が拒む。** 直した意味がここにある。
        var entry = server.InsertDraft();
        server.InsertLine(entry, 1, "debit", "1100", 1000);
        var thrown = Assert.Throws<SqliteException>(() => server.Execute(
            $"update journal_lines set sub_account_id = '' where journal_entry_id = {entry.Value}"));
        Assert.Contains("FOREIGN KEY", thrown.Message, StringComparison.Ordinal);

        // **NULL は受け取れる**（直した先が DB の受理集合の中にある）。
        server.Execute($"update journal_lines set sub_account_id = null where journal_entry_id = {entry.Value}");
    }

    /// <summary>
    /// <b>既に「無い」参照は触らない。</b>
    /// </summary>
    /// <remarks>
    /// 2 度目の保存では <c>Value</c> が <c>null</c> のまま届く。ここで例外にすると、
    /// <b>空にした行をもう一度保存できなくなる</b>。
    /// </remarks>
    [Fact]
    public async Task 既に無い参照はそのまま通す()
    {
        using var server = new AccountingServer();
        var line = SubmitData.LineWith(3, "SubAccount", new LinkFieldData { Value = null });

        await server.Gate.SubmitAsync([SubmitData.Adding(line)], NothingSaved);

        Assert.Null((line.Fields["SubAccount"] as LinkFieldData)?.Value);
    }

    /// <summary>
    /// <b>更新で空にした参照も直す。</b>
    /// </summary>
    /// <remarks>
    /// <b>参照を空に戻す操作は、ほとんど更新で起きる</b>——一度選んだものを消すのだから、
    /// その行はもう DB にある。<c>Add</c> だけ直すと、<b>実機で実際に踏む側が直らない</b>。
    /// </remarks>
    [Fact]
    public async Task 更新で空にした参照も無いに直す()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        var lineId = server.Scalar<long>($"select id from journal_lines where journal_entry_id = {id.Value}");
        var line = SubmitData.LineChanging(server.Text(lineId), "SubAccount", new LinkFieldData { Value = string.Empty });

        await server.Gate.SubmitAsync([SubmitData.Updating(line)], NothingSaved);

        Assert.Null((line.Fields["SubAccount"] as LinkFieldData)?.Value);
    }

    /// <summary>何も書かない保存（既に DB にある行を計上するときに使う）。</summary>
    // --- 元の伝票（qa/03 L-30）--------------------------------------------------------

    /// <summary>
    /// <b>元の伝票は、利用者が触ってよい場面が 1 つも無い欄</b>——取消・訂正の伝票はサーバが作るときに入れる。
    /// 画面は閲覧専用にしたので、来るのは画面を通らない経路（取込・API）。DDL のトリガに任せると定型文になる。
    /// </summary>
    [Fact]
    public async Task 新規の伝票に元の伝票を入れると断る()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 100), ("credit", "2200", 100));
        var entry = SubmitData.NewEntry(TemporaryId, status: "draft");
        entry.Fields["OriginalEntry"] = new LinkFieldData { Value = server.Text(original.Value) };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Adding(entry)], NothingSaved));

        Assert.Equal(
            "保存できません。元の伝票は、取消・訂正のときに自動で入ります。手で入れたり消したりはできません。",
            error.Message);
        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries where status = 'draft'"));
    }

    /// <summary>訂正の下書きの元の伝票を消そうとしても断る（消すと DDL の CHECK に当たり、定型文になる）。</summary>
    [Fact]
    public async Task 訂正の下書きの元の伝票を消すと断る()
    {
        using var server = new AccountingServer();
        var original = server.InsertPosted(1, null, "2026-08-24", ("debit", "1100", 100), ("credit", "2200", 100));
        var correction = server.InsertCorrectionDraft(original);
        var entry = SubmitData.Entry(server.Text(correction.Value));
        entry.Fields["OriginalEntry"] = new LinkFieldData { Value = string.Empty };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Updating(entry)], NothingSaved));

        Assert.Equal([JournalViolationCodes.OriginalEntrySystemAssigned], error.Violations.Select(v => v.Code));
        Assert.Equal(
            original.Value,
            server.Scalar<long>($"select original_entry_id from journal_entries where id = {correction.Value}"));
    }

    /// <summary>新規で元の伝票が空なら、画面がその欄を送ってきても通る（空の参照は無いに直される）。</summary>
    [Fact]
    public async Task 新規の伝票の元の伝票が空なら通る()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "draft");
        entry.Fields["OriginalEntry"] = new LinkFieldData { Value = string.Empty };

        await server.SubmitAsync([SubmitData.Adding(entry)], server.Saving(entry, Balanced));

        Assert.Equal(1, server.Scalar<long>("select count(*) from journal_entries"));
    }

    // --- 同時操作（qa/03 L-31）------------------------------------------------------

    /// <summary>
    /// <b>開いたあとに別の人が変えた伝票は、利用者の語で断る。</b> 対照——同じ版なら通る。
    /// </summary>
    /// <remarks>
    /// 楽観ロックそのものは CLB が効かせる（定型文で）。ここは**その前に**版を突き合わせて言葉を変える。
    /// **対照が無いと「たまたま失敗しただけ」と区別できない**（qa/03 L-31 の処方）。
    /// </remarks>
    [Fact]
    public async Task 開いたあとに別の人が変えた伝票は保存できない()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.Execute($"update journal_entries set description = '別の人が直した', optimistic_locking = 3 where id = {id.Value}");
        var stale = SubmitData.Entry(server.Text(id.Value));
        stale.Fields["OptimisticLocking"] = Version(2);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Updating(stale)], NothingSaved));

        Assert.Equal(
            "保存できません。この伝票は、あなたが開いたあとに別の人が変更しました。"
            + "画面を開き直して、その変更を確かめてから、もう一度操作してください。",
            error.Message);

        // **対照**：開き直して（版 3 を読んで）同じ操作をすると通る。
        var fresh = SubmitData.Entry(server.Text(id.Value));
        fresh.Fields["OptimisticLocking"] = Version(3);
        await server.SubmitAsync([SubmitData.Updating(fresh)], NothingSaved);
    }

    /// <summary>開いたあとに削除された伝票を保存しようとしたら、そう言う（「開き直せ」ではなく、一覧へ戻す）。</summary>
    [Fact]
    public async Task 開いたあとに削除された伝票は保存できない()
    {
        using var server = new AccountingServer();
        var gone = SubmitData.Entry("999");
        gone.Fields["OptimisticLocking"] = Version(0);

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Updating(gone)], NothingSaved));

        Assert.Equal(
            "保存できません。この伝票は、あなたが開いたあとに別の人が削除しました。振替伝票の一覧に戻ってください。"
            + "この内容が必要なら、新しい振替伝票として入力し直してください。",
            error.Message);
    }

    /// <summary>
    /// 明細には版が無いので、在るかだけを見る（変更でも削除でも）。<b>消えた明細は件数で束ねて 1 回だけ言う</b>——
    /// 差分に行番号は無いので行は指せない。同じ明細を変更と削除の両方に載せても 1 行と数える。
    /// </summary>
    [Fact]
    public async Task 開いたあとに削除された明細は件数で束ねて断る()
    {
        using var server = new AccountingServer();
        server.InsertDraft();
        var submit = new ModuleSubmitData
        {
            ModuleName = "JournalLine",
            Update =
            [
                SubmitData.LineChanging("998", "Amount", new NumberFieldData { Value = 5 }),
                SubmitData.LineChanging("999", "LineNo", new NumberFieldData { Value = 1 }),
            ],
            Delete =
            [
                new ModuleDeleteInfo { Id = "997", ModuleName = "JournalLine" },
                new ModuleDeleteInfo { Id = "999", ModuleName = "JournalLine" },
            ],
        };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([submit], NothingSaved));

        var violation = Assert.Single(error.Violations);
        Assert.Equal(JournalViolationCodes.DeletedByOthers, violation.Code);
        Assert.Equal(
            "明細 3 行が、あなたが開いたあとに別の人に削除されています。画面を開き直して、残っている明細を確かめてください。"
            + "伝票そのものが無ければ、振替伝票の一覧に戻ってください。",
            violation.Message);
    }

    /// <summary>伝票ごと消えていたら、明細のことは言わない（1 つの出来事に 2 つの断りを出さない。docs/21 §2-6）。</summary>
    [Fact]
    public async Task 伝票ごと消えていれば明細の断りは出さない()
    {
        using var server = new AccountingServer();
        var gone = SubmitData.Entry("999");
        gone.Fields["OptimisticLocking"] = Version(0);
        var line = new ModuleSubmitData
        {
            ModuleName = "JournalLine",
            Update = [SubmitData.LineChanging("998", "Amount", new NumberFieldData { Value = 5 })],
        };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Updating(gone), line], NothingSaved));

        Assert.Equal([JournalLineRules.DeletedByOthers], error.Violations.Select(v => v.Message));
    }

    /// <summary>
    /// 版の欄が差分に無いか、値が版として読めない（新規の <c>NullValue</c>・端数）なら判定しない
    /// （画面の更新は必ず数値で載せてくる。載せない経路は CLB が最後の砦）。**保存まで進んだことを見る。**
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("temporary-id")]
    public async Task 版の欄が無いか版として読めなければ同時操作は判定しない(string shape)
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry(shape == "temporary-id" ? TemporaryId : "999");
        switch (shape)
        {
            case "temporary-id":
                entry.Fields["OptimisticLocking"] = Version(0);
                break;
            case "null":
                entry.Fields["OptimisticLocking"] = new OptimisticLockingFieldData { Value = new NullValue() };
                break;
        }

        var saved = false;

        await server.SubmitAsync([SubmitData.Updating(entry)], () => { saved = true; return NothingSaved(); });

        Assert.True(saved);
    }

    /// <summary>
    /// 版の欄が<b>読めない型</b>で届いたら止める。黙って判定を落とすと、型が変わった日に断りが消える
    /// （<c>MasterSubmitGate</c> と同じ流儀。qa/03 L-42 の型）。
    /// </summary>
    [Theory]
    [InlineData("string-value")]
    [InlineData("text-field")]
    [InlineData("fraction")]
    public async Task 版の欄が読めない型や端数なら止まる(string shape)
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry("999");
        entry.Fields["OptimisticLocking"] = shape switch
        {
            "string-value" => new OptimisticLockingFieldData { Value = new StringValue { Value = "abc" } },
            "fraction" => new OptimisticLockingFieldData { Value = new DecimalValue { Value = 1.5m } },
            _ => new TextFieldData { Value = "abc" },
        };

        await AssertUnreadable(server, SubmitData.Updating(entry), "OptimisticLocking");
    }

    /// <summary><c>3.0</c> のような整数値は版として読む（decimal を文字列にしてから long に直していた回帰。R69-14）。</summary>
    [Fact]
    public async Task 版が小数点つきの整数値でも読める()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.Execute($"update journal_entries set optimistic_locking = 3 where id = {id.Value}");
        var stale = SubmitData.Entry(server.Text(id.Value));
        stale.Fields["OptimisticLocking"] = new OptimisticLockingFieldData { Value = new DecimalValue { Value = 2.0m } };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Updating(stale)], NothingSaved));

        Assert.Equal([JournalViolationCodes.ChangedByOthers], error.Violations.Select(v => v.Code));

        var fresh = SubmitData.Entry(server.Text(id.Value));
        fresh.Fields["OptimisticLocking"] = new OptimisticLockingFieldData { Value = new DecimalValue { Value = 3.0m } };
        var saved = false;
        await server.SubmitAsync([SubmitData.Updating(fresh)], () => { saved = true; return NothingSaved(); });
        Assert.True(saved);
    }

    /// <summary>識別子の欄が読めない型なら止める（黙って空文字にすると、同時操作も行番号も判定から外れる）。値が無いのは「無い」として扱う。</summary>
    [Theory]
    [InlineData("text-field")]
    [InlineData("null-value")]
    public async Task 識別子の欄が読めない型なら止まる(string shape)
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry("999");
        entry.Fields["OptimisticLocking"] = Version(0);

        if (shape == "text-field")
        {
            entry.Fields["Id"] = new TextFieldData { Value = "999" };
            await AssertUnreadable(server, SubmitData.Updating(entry), "Id");
            return;
        }

        entry.Fields["Id"] = new IdFieldData { Value = null };
        var saved = false;
        await server.SubmitAsync([SubmitData.Updating(entry)], () => { saved = true; return NothingSaved(); });
        Assert.True(saved);
    }

    /// <summary>
    /// <b>別の人が先に消した伝票の削除は、削除の言葉で断る。</b> 素通しにすると CLB 本来の削除が失敗し、
    /// 削除なのに「入力内容を確かめ…」の定型文が出る（2026-09-10 実測 1.3.20。qa/01 F-41）。
    /// </summary>
    [Fact]
    public async Task 別の人が先に消した伝票の削除は削除の言葉で断る()
    {
        using var server = new AccountingServer();
        var untouched = server.InsertDraft();

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Deleting("999")], NothingSaved));

        Assert.Equal(
            "削除できません。この伝票は、あなたが開いたあとに別の人が削除しました。振替伝票の一覧に戻ってください。",
            error.Message);
        Assert.Equal(untouched.Value, server.Scalar<long>("select id from journal_entries"));
    }

    /// <summary>別の人が変えた伝票の削除も断る（削除の差分は版を運んでくる）。対照——同じ版なら消える。</summary>
    [Fact]
    public async Task 別の人が変えた伝票の削除は断り_同じ版なら消える()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.Execute($"update journal_entries set optimistic_locking = 3 where id = {id.Value}");

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Deleting(server.Text(id.Value), 2)], NothingSaved));

        Assert.StartsWith("削除できません。この伝票は、あなたが開いたあとに別の人が変更しました。", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.Scalar<long>("select count(*) from journal_entries"));

        await server.SubmitAsync([SubmitData.Deleting(server.Text(id.Value), 3)], server.Deleting(id));

        Assert.Equal(0, server.Scalar<long>("select count(*) from journal_entries"));
    }

    // --- 明細の行番号の重複（正典は DDL の UNIQUE (journal_entry_id, line_no)）--------------------

    /// <summary>
    /// <b>保存されている明細と差分を合わせた姿で数える。</b> 差分だけでは判定できない（<c>UNIQUE (journal_entry_id, line_no)</c>）。
    /// 番号は文に埋める（「1 行目:」とは言わない——1 行目が 2 つあることが読めない）。
    /// </summary>
    [Fact]
    public async Task 保存されている明細と同じ行番号に変えると断る()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        server.InsertLine(id, 2, "credit", "2200", 100);
        var second = server.LineIdOf(id, 2);
        var submit = new ModuleSubmitData
        {
            ModuleName = "JournalLine",
            Update = [SubmitData.LineChanging(server.Text(second), "LineNo", new NumberFieldData { Value = 1 })],
        };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([submit], NothingSaved));

        var violation = Assert.Single(error.Violations);
        Assert.Equal(JournalViolationCodes.LineNoInvalid, violation.Code);
        Assert.Null(violation.LineNo);
        Assert.Equal("行番号 1 が 2 つの明細に付いています。行番号は画面が自動で振るので、明細を入力し直してください。", violation.Message);
    }

    /// <summary>
    /// <b>番号の入れ替え（1↔2）は、最終形に重複が無くても断る。</b> CLB は UPDATE を 1 行ずつ流し、SQLite の UNIQUE は
    /// 遅延できないので、最初の UPDATE で落ちる（関門が通して DB が拒む形。qa/03 L-21）。対照——空いている番号へ動かすのは通る。
    /// </summary>
    [Fact]
    public async Task 行番号の入れ替えは断り_空いている番号へ動かすのは通る()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        server.InsertLine(id, 2, "credit", "2200", 100);
        var first = server.LineIdOf(id, 1);
        var second = server.LineIdOf(id, 2);
        var swap = new ModuleSubmitData
        {
            ModuleName = "JournalLine",
            Update =
            [
                SubmitData.LineChanging(server.Text(first), "LineNo", new NumberFieldData { Value = 2 }),
                SubmitData.LineChanging(server.Text(second), "LineNo", new NumberFieldData { Value = 1 }),
            ],
        };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([swap], NothingSaved));

        Assert.Equal(
            [JournalLineRules.LineNoDuplicatedAt(1), JournalLineRules.LineNoDuplicatedAt(2)],
            error.Violations.Select(v => v.Message));

        // 空いている番号へ動かす。同じ番号を送り直す行（画面は触っていない欄も送ることがある。qa/01 F-12）は変更ではない。
        var move = new ModuleSubmitData
        {
            ModuleName = "JournalLine",
            Update =
            [
                SubmitData.LineChanging(server.Text(first), "LineNo", new NumberFieldData { Value = 1 }),
                SubmitData.LineChanging(server.Text(second), "LineNo", new NumberFieldData { Value = 3 }),
            ],
        };
        var saved = false;
        await server.SubmitAsync([move], () => { saved = true; return NothingSaved(); });
        Assert.True(saved);
    }

    /// <summary>
    /// <b>順序が分かっていない組み合わせは保守側に倒す</b>（Delete と Update、Update と Add の順序は未実測。qa/01 F-41）——
    /// 消す行の番号へ別の行を動かす、番号を空けてそこへ足す、のどちらも断る。
    /// </summary>
    [Theory]
    [InlineData("renumber-onto-deleted")]
    [InlineData("add-onto-renumbered")]
    public async Task 順序の分からない番号の使い回しは断る(string shape)
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        server.InsertLine(id, 2, "credit", "2200", 100);
        var first = server.LineIdOf(id, 1);
        var second = server.LineIdOf(id, 2);
        var added = SubmitData.Line(1);
        added.Fields["JournalEntryId"] = new IdFieldData { Value = server.Text(id.Value) };
        var submit = shape == "renumber-onto-deleted"
            ? new ModuleSubmitData
            {
                ModuleName = "JournalLine",
                Update = [SubmitData.LineChanging(server.Text(second), "LineNo", new NumberFieldData { Value = 1 })],
                Delete = [new ModuleDeleteInfo { Id = server.Text(first), ModuleName = "JournalLine" }],
            }
            : new ModuleSubmitData
            {
                ModuleName = "JournalLine",
                Update = [SubmitData.LineChanging(server.Text(first), "LineNo", new NumberFieldData { Value = 3 })],
                Add = [added],
            };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([submit], NothingSaved));

        Assert.Equal([JournalLineRules.LineNoDuplicatedAt(1)], error.Violations.Select(v => v.Message));
    }

    /// <summary>
    /// 追加する行が保存されている行と同じ番号なら断る。消してから同じ番号を足すのは通る（対照。CLB は Delete を Add より先に流す）。
    /// </summary>
    /// <remarks>
    /// <b>親の識別子は <c>IdFieldData</c> で届く</b>（明細の親 FK は <c>IdFieldDesign</c>。2026-09-10 実測 1.3.20。qa/01 F-41）。
    /// 検体を <c>LinkFieldData</c> だけで組んでいたので、本番の形では追加の判定が一度も効いていなかった（qa/03 L-42）。
    /// <c>LinkFieldData</c> は対照として残す。
    /// </remarks>
    [Theory]
    [InlineData("id")]
    [InlineData("link")]
    public async Task 保存されている明細と同じ行番号を足すと断り_消してから足すのは通る(string parentShape)
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        server.InsertLine(id, 2, "credit", "2200", 100);
        var second = server.LineIdOf(id, 2);
        var added = SubmitData.Line(2);
        added.Fields["JournalEntryId"] = Parent(parentShape, server.Text(id.Value));

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([new ModuleSubmitData { ModuleName = "JournalLine", Add = [added] }], NothingSaved));
        Assert.Equal([JournalLineRules.LineNoDuplicatedAt(2)], error.Violations.Select(v => v.Message));

        // **対照**：2 行目を消して、同じ番号で足す（CLB は Delete を Add より先に流す。qa/01 F-41 ⑤）。
        var saved = false;
        await server.SubmitAsync(
            [
                new ModuleSubmitData
                {
                    ModuleName = "JournalLine",
                    Add = [added],
                    Delete = [new ModuleDeleteInfo { Id = server.Text(second), ModuleName = "JournalLine" }],
                },
            ],
            () => { saved = true; return NothingSaved(); });
        Assert.True(saved);
    }

    /// <summary>新しい伝票（仮 ID）の明細は、差分の追加だけで数える。親の欄には伝票と同じ仮 ID が入って届く（qa/01 F-41）。</summary>
    [Fact]
    public async Task 新しい伝票の明細の行番号が重なれば断る()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry(TemporaryId, status: "draft");
        var first = SubmitData.Line(3);
        var second = SubmitData.Line(3);
        second.Fields["Id"] = new IdFieldData { Value = "@temporary:line3b" };
        first.Fields["JournalEntryId"] = new IdFieldData { Value = TemporaryId };
        second.Fields["JournalEntryId"] = new IdFieldData { Value = TemporaryId };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([SubmitData.Adding(entry, first, second)], NothingSaved));

        var violation = Assert.Single(error.Violations);
        Assert.Equal(JournalViolationCodes.LineNoInvalid, violation.Code);
        Assert.Equal(JournalLineRules.LineNoDuplicatedAt(3), violation.Message);
    }

    /// <summary>親の欄が読めない型で届いたら止める（黙って落とすと、追加の判定が消える。qa/03 L-42）。</summary>
    [Fact]
    public async Task 明細の親の欄が読めない型なら止まる()
    {
        using var server = new AccountingServer();
        var added = SubmitData.LineWith(1, "JournalEntryId", new TextFieldData { Value = "1" });

        await AssertUnreadable(server, new ModuleSubmitData { ModuleName = "JournalLine", Add = [added] }, "JournalEntryId");
    }

    /// <summary>
    /// 識別子は数値に直してから突き合わせる——字面のまま鍵にすると <c>"02"</c> と <c>"2"</c> が別の行になる。
    /// 明細の側（消す行が消したと数えられず、足す番号が偽の重複になる）と、伝票の側（<c>"067"</c> の親に足す行が別の伝票に数えられ、重複を見逃す）。
    /// </summary>
    [Theory]
    [InlineData("line")]
    [InlineData("entry")]
    public async Task 識別子は字面が違っても同じ行と見る(string where)
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        server.InsertLine(id, 2, "credit", "2200", 100);
        var added = SubmitData.Line(1);
        added.Fields["JournalEntryId"] = new IdFieldData { Value = (where == "entry" ? "0" : "") + server.Text(id.Value) };
        var submit = new ModuleSubmitData { ModuleName = "JournalLine", Add = [added] };
        if (where == "line")
        {
            submit.Delete = [new ModuleDeleteInfo { Id = "0" + server.Text(server.LineIdOf(id, 1)), ModuleName = "JournalLine" }];
        }

        if (where == "line")
        {
            // 消す行の番号を足す——消したと数えられれば通る。
            var saved = false;
            await server.SubmitAsync([submit], () => { saved = true; return NothingSaved(); });
            Assert.True(saved);
        }
        else
        {
            // 親を "0" 付きで指しても同じ伝票——保存済みの 1 行目と重なる。
            var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
                () => server.SubmitAsync([submit], NothingSaved));
            Assert.Equal([JournalLineRules.LineNoDuplicatedAt(1)], error.Violations.Select(v => v.Message));
        }
    }

    /// <summary>行番号の無い追加は、必須の断りだけで止まる（重複の判定には載らない）。</summary>
    [Fact]
    public async Task 行番号の無い明細の追加は必須の断りだけで止まる()
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        var added = SubmitData.LineWithout(1, "LineNo");
        added.Fields["JournalEntryId"] = new IdFieldData { Value = server.Text(id.Value) };

        var error = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => server.SubmitAsync([new ModuleSubmitData { ModuleName = "JournalLine", Add = [added] }], NothingSaved));

        Assert.Equal([JournalViolationCodes.RequiredValueMissing], error.Violations.Select(v => v.Code));
    }

    /// <summary>
    /// 行番号の重複は「どの伝票の何行目か」が分かる差分だけで数える。識別子が仮のままの変更や、伝票を指していない追加は数えない
    /// （数えられないものを断ると、正当な保存が止まる。DDL の UNIQUE が最後の砦）。**保存まで進んだことを見る。**
    /// </summary>
    /// <remarks>仮 ID の明細の削除は画面からは来ない（取込・API の形）。</remarks>
    [Theory]
    [InlineData("update-temporary-id")]
    [InlineData("delete-temporary-id")]
    [InlineData("add-without-entry")]
    public async Task 伝票を特定できない明細は行番号の重複を数えない(string shape)
    {
        using var server = new AccountingServer();
        var id = server.InsertDraft();
        server.InsertLine(id, 1, "debit", "1100", 100);
        var submit = shape switch
        {
            "update-temporary-id" => new ModuleSubmitData
            {
                ModuleName = "JournalLine",
                Update = [SubmitData.LineChanging("@temporary:x", "LineNo", new NumberFieldData { Value = 1 })],
            },
            "delete-temporary-id" => new ModuleSubmitData
            {
                ModuleName = "JournalLine",
                Delete = [new ModuleDeleteInfo { Id = "@temporary:x", ModuleName = "JournalLine" }],
            },
            _ => new ModuleSubmitData
            {
                ModuleName = "JournalLine",
                Add = [SubmitData.LineWith(1, "JournalEntryId", new IdFieldData { Value = string.Empty })],
            },
        };
        var saved = false;

        await server.SubmitAsync([submit], () => { saved = true; return NothingSaved(); });

        Assert.True(saved);
    }

    /// <summary>画面が載せてくる形の版（<c>OptimisticLockingFieldData</c> の <c>DecimalValue</c>。qa/01 F-41）。</summary>
    private static OptimisticLockingFieldData Version(long version)
        => new() { Value = new DecimalValue { Value = version } };

    private static FieldDataBase Parent(string shape, string value)
        => shape == "id" ? new IdFieldData { Value = value } : new LinkFieldData { Value = value };

    /// <summary>
    /// 読めない型は<b>利用者の誤りではない</b>ので、画面には定型文だけを見せ、欄の名前はホストのログへ回す
    /// （<c>AccountingSubmitPipeline</c>。<c>MasterSubmitGateTests.AssertUnreadable</c> と同じ見方）。保存まで進まない。
    /// </summary>
    private static async Task AssertUnreadable(AccountingServer server, ModuleSubmitData submitted, string field)
    {
        var saved = false;
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SubmitAsync([submitted], () => { saved = true; return NothingSaved(); }));

        Assert.Equal(SaveFailureMessage.Text, thrown.Message);
        Assert.DoesNotContain(field, thrown.Message, StringComparison.Ordinal);
        Assert.Contains(field, Assert.Single(server.SaveFailureLog), StringComparison.Ordinal);
        Assert.False(saved);
    }

    private static Task<List<ModuleSubmitResult>> NothingSaved() => Task.FromResult(new List<ModuleSubmitResult>());
}
