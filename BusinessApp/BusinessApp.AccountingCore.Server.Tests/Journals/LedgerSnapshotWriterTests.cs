namespace BusinessApp.AccountingCore.Server.Tests.Journals;

using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

/// <summary>
/// 計上のときに帳簿の記載事項を写して固定する（ADR-0018・docs/13 §4）。
/// </summary>
/// <remarks>
/// <b>本物の DDL・本物のトリガに当てて検査する。</b> 「計上済みにする前に焼く」という順番は、
/// トリガのある DB でしか壊れないので、模造の保存先では守れているか分からない。
/// </remarks>
public class LedgerSnapshotWriterTests
{
    private const string RegistrationNo = "T1234567890123";

    /// <summary>取引先を 1 件入れて識別子を返す。</summary>
    private static long InsertPartner(AccountingServer server, string code, string name)
    {
        server.Execute($"insert into partners (code, name, is_active) values ('{code}', '{name}', 1)");
        return server.Scalar<long>("select last_insert_rowid()");
    }

    private static void InsertRegistration(
        AccountingServer server, long partnerId, string no, string validFrom, string? endedOn = null, string? reason = null)
    {
        var ended = endedOn is null ? "null" : AccountingServer.DateLiteral(endedOn);
        var endReason = reason is null ? "null" : $"'{reason}'";
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from, ended_on, end_reason)
            values ({partnerId}, '{no}', {AccountingServer.DateLiteral(validFrom)}, {ended}, {endReason})
            """);
    }

    private static void SetLinePartner(AccountingServer server, long entryId, int lineNo, long? partnerId, string? taxPoint)
    {
        var partner = partnerId is long value ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        var point = taxPoint is null ? "null" : AccountingServer.DateLiteral(taxPoint);
        server.Execute($"""
            update journal_lines set partner_id = {partner}, tax_point = {point}
             where journal_entry_id = {entryId} and line_no = {lineNo}
            """);
    }

    /// <summary>伝票（ヘッダ）に焼かれた取引先名。<b>明細の写しとは別の列である</b>（ADR-0037）。</summary>
    private static string? EntrySnapshot(AccountingServer server, long entryId)
        => server.Scalar<string?>(
            $"select partner_name_snapshot from journal_entries where id = {entryId}");

    private static void SetEntryPartner(AccountingServer server, long entryId, long partnerId)
        => server.Execute($"update journal_entries set partner_id = {partnerId} where id = {entryId}");

    private static string? Snapshot(AccountingServer server, long entryId, int lineNo, string column)
        => server.Scalar<string?>(
            $"select {column} from journal_lines where journal_entry_id = {entryId} and line_no = {lineNo}");

    /// <summary>
    /// 下書きを 1 件作り、明細 2 行を入れて計上できる形にする。
    /// </summary>
    /// <remarks>
    /// <b>取引日と計上日をずらしてある。</b> 揃えると、課税仕入れの時点の既定を
    /// 取引日にしても計上日にしても同じ結果になり、どちらで引いているかを検査できない
    /// （2026-08-26 の自己レビュー指摘。qa/02）。
    /// </remarks>
    private static long PostableDraft(AccountingServer server)
    {
        var id = server.InsertDraft(transactionDate: "2026-08-19", postingDate: "2026-08-24");
        server.InsertLine(id, 1, "debit", "1100", 1000);
        server.InsertLine(id, 2, "credit", "1110", 1000);
        return id.Value;
    }

    private static async Task PostAsync(AccountingServer server, long entryId)
    {
        var draft = await server.EntryStore.LoadAsync(new(entryId));
        await server.Poster.PostAsync(draft, await server.MasterLoader.LoadAsync());
    }

    [Fact]
    public async Task 明細の取引先の名称を計上時に写す()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 1, "partner_name_snapshot"));
    }

    /// <summary>
    /// <b>計上したあとに改名しても、帳簿の記載は変わらない。</b>
    /// これが ADR-0018 が写しを持つ理由そのものである。
    /// </summary>
    [Fact]
    public async Task 計上後に改名しても写しは変わらない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-24");

        await PostAsync(server, entry);
        server.Execute($"update partners set name = '株式会社アルタイル' where id = {partner}");

        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 1, "partner_name_snapshot"));
    }

    /// <summary>明細に取引先が無い行には、<b>伝票の取引先</b>を写す（ADR-0018・docs/10 §4-1）。</summary>
    [Fact]
    public async Task 明細に取引先が無い行には伝票の取引先を写す()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        var entry = PostableDraft(server);
        server.Execute($"update journal_entries set partner_id = {partner} where id = {entry}");
        SetLinePartner(server, entry, 1, null, "2026-08-24");
        SetLinePartner(server, entry, 2, null, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 1, "partner_name_snapshot"));
        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 2, "partner_name_snapshot"));
    }

    /// <summary>明細の取引先が伝票と違うときは、<b>明細のほうが勝つ</b>。</summary>
    [Fact]
    public async Task 明細の取引先が伝票より優先される()
    {
        using var server = new AccountingServer();
        var header = InsertPartner(server, "P900", "伝票の取引先");
        var line = InsertPartner(server, "P901", "明細の取引先");
        var entry = PostableDraft(server);
        server.Execute($"update journal_entries set partner_id = {header} where id = {entry}");
        SetLinePartner(server, entry, 1, line, "2026-08-24");
        SetLinePartner(server, entry, 2, null, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Equal("明細の取引先", Snapshot(server, entry, 1, "partner_name_snapshot"));
        Assert.Equal("伝票の取引先", Snapshot(server, entry, 2, "partner_name_snapshot"));

        // **伝票の写しは明細の写しではない**（ADR-0037 §1）。ここが 2 つの列の役割を分ける唯一の検体で、
        // 他のテストは伝票と明細の取引先が同じなので、取り違えても緑になる。
        Assert.Equal("伝票の取引先", EntrySnapshot(server, entry));
    }

    /// <summary>
    /// <b>取引先の無い行には NULL を焼く（飛ばさない）。</b>
    /// 飛ばすと、利用者が送ってきた文字列がそのまま帳簿の記載として残る
    /// （2026-08-26 の自己レビュー指摘。qa/02）。
    /// </summary>
    [Fact]
    public async Task 取引先の無い行には写しを残さない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-19");

        // 利用者が写しの欄に勝手な値を入れて送ってきた状況を作る。
        server.Execute($"""
            update journal_lines
               set partner_name_snapshot = '利用者が書いた名前', registration_no_snapshot = 'T9999999999999'
             where journal_entry_id = {entry} and line_no = 2
            """);

        await PostAsync(server, entry);

        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 1, "partner_name_snapshot"));
        Assert.Null(Snapshot(server, entry, 2, "partner_name_snapshot"));
        Assert.Null(Snapshot(server, entry, 2, "registration_no_snapshot"));
    }

    /// <summary>
    /// <b>他の伝票の明細を巻き込まない。</b> 更新の条件から伝票の識別子が落ちても、
    /// 伝票が 1 件しか無い検査では気づけない。
    /// </summary>
    [Fact]
    public async Task 他の伝票の明細には触らない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        var other = PostableDraft(server);
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-19");

        await PostAsync(server, entry);

        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 1, "partner_name_snapshot"));
        Assert.Null(Snapshot(server, other, 1, "partner_name_snapshot"));
    }

    [Fact]
    public async Task 課税仕入れの時点の登録番号を写す()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Equal(RegistrationNo, Snapshot(server, entry, 1, "registration_no_snapshot"));
    }

    /// <summary>
    /// <b>登録より前の課税仕入れには写さない。</b> 現在のマスタから引くと、
    /// 登録前の取引に登録番号が付いてしまう（ADR-0018 が写しを持つもう 1 つの理由）。
    /// </summary>
    [Fact]
    public async Task 登録より前の課税仕入れには登録番号を写さない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2026-09-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 1, "partner_name_snapshot"));
        Assert.Null(Snapshot(server, entry, 1, "registration_no_snapshot"));
    }

    [Fact]
    public async Task 取り消された後の課税仕入れには登録番号を写さない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01", "2026-08-20", "revoked");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Null(Snapshot(server, entry, 1, "registration_no_snapshot"));
    }

    /// <summary>
    /// <b>他の取引先の登録を引かない。</b> 登録の読み出しが取引先で絞れていないと、
    /// 他社の番号が明細に焼き込まれる（計上済みは不変なので直せない）。
    /// </summary>
    [Fact]
    public async Task 他の取引先の登録は引かない()
    {
        using var server = new AccountingServer();
        var mine = InsertPartner(server, "P900", "自分の取引先");
        var other = InsertPartner(server, "P901", "別の取引先");
        InsertRegistration(server, mine, "T1111111111111", "2023-10-01");
        InsertRegistration(server, other, "T2222222222222", "2023-10-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, mine, "2026-08-19");

        await PostAsync(server, entry);

        Assert.Equal("T1111111111111", Snapshot(server, entry, 1, "registration_no_snapshot"));
    }

    /// <summary>
    /// <b>課税仕入れの時点が入っていなければ取引日で引く（計上日ではない）。</b>
    /// いまの振替伝票の画面はこの項目を入力させないので、
    /// 「無ければ写さない」にすると写しが永久に空になる（qa/03 L-12）。
    /// </summary>
    /// <remarks>
    /// 登録は取引日（08-19）には生きていて、計上日（08-24）には終わっている。
    /// <b>計上日で引く実装に変えると、この検査だけが赤くなる。</b>
    /// </remarks>
    [Fact]
    public async Task 課税仕入れの時点が無い行は取引日で引く()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01", "2026-08-20", "revoked");
        var entry = PostableDraft(server);        // 取引日 2026-08-19 / 計上日 2026-08-24
        SetLinePartner(server, entry, 1, partner, taxPoint: null);

        await PostAsync(server, entry);

        Assert.Equal("株式会社ベガ商会", Snapshot(server, entry, 1, "partner_name_snapshot"));
        Assert.Equal(RegistrationNo, Snapshot(server, entry, 1, "registration_no_snapshot"));
    }

    /// <summary>取引日で引くので、<b>取引日の時点で終わっていれば写さない</b>。</summary>
    [Fact]
    public async Task 課税仕入れの時点が無く取引日に登録が無ければ写さない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01", "2026-08-15", "revoked");
        var entry = PostableDraft(server);        // 取引日 2026-08-19
        SetLinePartner(server, entry, 1, partner, taxPoint: null);

        await PostAsync(server, entry);

        Assert.Null(Snapshot(server, entry, 1, "registration_no_snapshot"));
    }

    /// <summary>
    /// <b>この検査がいちばん大事。</b> 写しを焼くのが計上済みにしたあとだと、
    /// DDL のトリガが UPDATE を止めて<b>計上そのものが落ちる</b>。
    /// 「計上できて、しかも写しが入っている」ことを両方見る。
    /// </summary>
    [Fact]
    public async Task 写しを焼いても計上そのものは通る()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Equal("posted", server.Scalar<string>($"select status from journal_entries where id = {entry}"));
        Assert.Equal(1, server.Scalar<long>($"select entry_no is not null from journal_entries where id = {entry}"));
        Assert.Equal(RegistrationNo, Snapshot(server, entry, 1, "registration_no_snapshot"));
    }

    /// <summary>同じ取引先が何行にも出ても、写しは全部の行に入る。</summary>
    [Fact]
    public async Task 同じ取引先が複数行にあっても全部に写す()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-24");
        SetLinePartner(server, entry, 2, partner, "2026-08-24");

        await PostAsync(server, entry);

        Assert.Equal(RegistrationNo, Snapshot(server, entry, 1, "registration_no_snapshot"));
        Assert.Equal(RegistrationNo, Snapshot(server, entry, 2, "registration_no_snapshot"));
    }

    /// <summary>
    /// 同じ取引先でも、<b>行ごとの課税仕入れの時点で引く</b>。
    /// 取引先ごとに 1 回読む作りにしているが、判定は行ごとであることを固定する。
    /// </summary>
    [Fact]
    public async Task 行ごとの課税仕入れの時点で引く()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01", "2026-08-20", "revoked");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-19");   // 取消の前
        SetLinePartner(server, entry, 2, partner, "2026-08-21");   // 取消の後

        await PostAsync(server, entry);

        Assert.Equal(RegistrationNo, Snapshot(server, entry, 1, "registration_no_snapshot"));
        Assert.Null(Snapshot(server, entry, 2, "registration_no_snapshot"));
    }

    [Fact]
    public async Task 保存されていない仕訳には写しを焼けない()
    {
        using var server = new AccountingServer();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SnapshotWriter.BurnAsync(new JournalEntry
            {
                Id = null,
                FiscalYearId = AccountingServer.FiscalYear,
                TransactionDate = new DateOnly(2026, 8, 24),
                PostingDate = new DateOnly(2026, 8, 24),
                Status = EntryStatus.Draft,
                EntryType = EntryType.Normal,
                EnteredAt = AccountingServer.Now,
                Lines = [],
            }));

        Assert.Contains("保存されていない", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 伝票を渡さなければ止まる()
    {
        using var server = new AccountingServer();
        await Assert.ThrowsAsync<ArgumentNullException>(() => server.SnapshotWriter.BurnAsync(null!));
    }

    /// <summary>
    /// <b>取消は原仕訳の写しをそのまま引き継ぐ</b>（ADR-0018）。
    /// </summary>
    /// <remarks>
    /// 焼き直すと、原仕訳の計上後に改名された相手で<b>同じ取引の表と裏が違う名前になる</b>
    /// （実機操作テストで発見。qa/03 L-13）。名称も登録番号も引き継ぐ。
    /// </remarks>
    [Fact]
    public async Task 取消は原仕訳の写しを引き継ぐ()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "計上したときの名前");
        InsertRegistration(server, partner, RegistrationNo, "2023-10-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-19");
        SetLinePartner(server, entry, 2, partner, "2026-08-19");
        await PostAsync(server, entry);

        // 計上のあとで改名し、登録も取り消す。
        server.Execute($"update partners set name = '取り消すときの名前' where id = {partner}");
        server.Execute($"""
            update partner_invoice_registrations
               set ended_on = {AccountingServer.DateLiteral("2026-08-18")}, end_reason = 'revoked'
             where partner_id = {partner}
            """);

        var reversal = await server.AmendAsync(s => s.ReverseAsync(new(entry)));

        Assert.Equal("計上したときの名前", Snapshot(server, reversal.Value, 1, "partner_name_snapshot"));
        Assert.Equal(RegistrationNo, Snapshot(server, reversal.Value, 1, "registration_no_snapshot"));
        Assert.Equal("計上したときの名前", Snapshot(server, reversal.Value, 2, "partner_name_snapshot"));
    }

    /// <summary>
    /// <b>写しが書けなければ計上そのものが残らない。</b>
    /// </summary>
    /// <remarks>
    /// 焼くのを計上済みにする前に置いているのは、この性質のためである。
    /// 巻き戻らないと、写しの空いた計上済みが永久に残る（計上済みは不変。ADR-0004）。
    /// </remarks>
    [Fact]
    public async Task 写しが書けなければ計上も残らない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-19");

        server.FailBeforeStatement = sql =>
            sql.Contains("registration_no_snapshot", StringComparison.Ordinal)
                ? new InvalidOperationException("写しの書き込みを落とす")
                : null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.AmendAsync(async _ =>
            {
                var draft = await server.EntryStore.LoadAsync(new(entry));
                return await server.Poster.PostAsync(draft, await server.MasterLoader.LoadAsync());
            }));

        server.FailBeforeStatement = null;

        Assert.Equal("draft", server.Scalar<string>($"select status from journal_entries where id = {entry}"));
        Assert.Null(server.Scalar<long?>($"select entry_no from journal_entries where id = {entry}"));
        Assert.Null(Snapshot(server, entry, 1, "partner_name_snapshot"));

        // **伝票の列は明細より先に書かれる**（LedgerSnapshotWriter）。
        // ここを見ないと、「途中まで書けた写しが残る」新しい経路が緑のまま通る。
        Assert.Null(EntrySnapshot(server, entry));
    }

    /// <summary>
    /// <b>どの登録を写すか決められないときは、利用者のことばで計上を止める。</b>
    /// </summary>
    /// <remarks>
    /// <para>入力の時点で止めるのが本筋（<c>PartnerRegistrationSubmitGate</c>）で、
    /// <b>2026-08-31 からは DB の一意索引も同じ組を拒む</b>（qa/02 R26-20）。
    /// ここが守るのは、<b>その索引より前に入った行</b>と、索引を持たない DB から
    /// 移してきた行である。<b>黙ってどちらかを選ばない</b>ことを固定する。</para>
    /// <para><b>だから検体を作るには索引を外すしかない。</b> 関門を迂回しているのではなく、
    /// 「守りが 1 枚も無かった時代のデータ」を作っている。索引はこのテストの中だけで消える
    /// （インメモリ DB なので、閉じれば元に戻る）。</para>
    /// </remarks>
    [Fact]
    public async Task 登録が同じ日に_2_件あれば計上を止める()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        server.Execute("drop index ux_partner_invoice_registrations_valid_from");
        InsertRegistration(server, partner, "T1111111111111", "2023-10-01");
        InsertRegistration(server, partner, "T2222222222222", "2023-10-01");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-19");

        var thrown = await Assert.ThrowsAsync<JournalPostingRejectedException>(
            () => PostAsync(server, entry));

        Assert.Contains("株式会社ベガ商会", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("登録番号", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", server.Scalar<string>($"select status from journal_entries where id = {entry}"));
    }

    /// <summary>
    /// <b>写しの書き込みが 1 行に当たらなければ止める。</b>
    /// 黙って 0 件で通すと、写しが空のまま計上済みになって永久に直せない。
    /// </summary>
    [Fact]
    public async Task 写しが_1_行に当たらなければ止まる()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        var entry = PostableDraft(server);
        SetLinePartner(server, entry, 1, partner, "2026-08-19");

        var draft = await server.EntryStore.LoadAsync(new(entry));

        // DB から明細を消して、UPDATE が 1 行にも当たらない状況を作る。
        server.Execute($"delete from journal_lines where journal_entry_id = {entry}");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SnapshotWriter.BurnAsync(draft));

        Assert.Contains("写しを書けなかった", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>伝票の取引先も計上時に焼く</b>（ADR-0037）。計上済みの伝票は不変（I-05）なのに、
    /// 参照先を改名すると画面の表示だけが動く——それを止めるための列である。
    /// </summary>
    [Fact]
    public async Task 伝票の取引先の名前を計上時に写し_改名しても動かない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "計上したときの名前");
        var entry = PostableDraft(server);
        SetEntryPartner(server, entry, partner);

        await PostAsync(server, entry);
        server.Execute($"update partners set name = '改名したあとの名前' where id = {partner}");

        Assert.Equal("計上したときの名前", EntrySnapshot(server, entry));
    }

    [Fact]
    public async Task 取引先の無い伝票には写しを残さない()
    {
        using var server = new AccountingServer();
        var entry = PostableDraft(server);

        await PostAsync(server, entry);

        Assert.Null(EntrySnapshot(server, entry));
    }

    /// <summary>
    /// 取消は<b>伝票の写しも</b>原仕訳から引き継ぐ。焼き直すと表と裏で名前が変わる。
    /// </summary>
    [Fact]
    public async Task 取消は伝票の写しも引き継ぐ()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "計上したときの名前");
        var entry = PostableDraft(server);
        SetEntryPartner(server, entry, partner);
        await PostAsync(server, entry);

        server.Execute($"update partners set name = '取り消すときの名前' where id = {partner}");

        var reversal = await server.AmendAsync(s => s.ReverseAsync(new(entry)));

        Assert.Equal("計上したときの名前", EntrySnapshot(server, reversal.Value));
    }

    /// <summary>
    /// <b>訂正の再計上は焼き直す。</b> 取消と違い、利用者が取引先を直せるからである。
    /// </summary>
    /// <remarks>
    /// これが <c>EntryType.Reversal</c> の分岐の<b>反対側</b>である。
    /// 条件を <c>Reversal or Correction</c> に書き換えても、この検体が無ければ全部緑になる——
    /// そのとき再計上には<b>原仕訳の取引先の名前</b>が焼かれ、伝票の取引先と写しが
    /// 食い違ったまま計上済み＝不変になる。
    /// </remarks>
    [Fact]
    public async Task 訂正の再計上は取引先を焼き直す()
    {
        using var server = new AccountingServer();
        var before = InsertPartner(server, "P900", "訂正する前の取引先");
        var after = InsertPartner(server, "P901", "訂正したあとの取引先");
        var entry = PostableDraft(server);
        SetEntryPartner(server, entry, before);
        SetLinePartner(server, entry, 1, before, "2026-08-19");
        await PostAsync(server, entry);

        // 訂正すると、取消が計上され、原仕訳を写した下書き（再計上）が返る（ADR-0015）。
        // その下書きの取引先を直して計上する。
        var started = await server.AmendAsync(s => s.CorrectAsync(new(entry)));
        var correction = started.CorrectionId.Value;
        SetEntryPartner(server, correction, after);
        SetLinePartner(server, correction, 1, after, "2026-08-19");
        await PostAsync(server, correction);

        Assert.Equal("訂正したあとの取引先", EntrySnapshot(server, correction));
        Assert.Equal("訂正したあとの取引先", Snapshot(server, correction, 1, "partner_name_snapshot"));

        // 取消のほうは原仕訳から引き継ぐ（焼き直さない）。分岐の両側をこの 1 本で固定する。
        Assert.Equal("訂正する前の取引先", EntrySnapshot(server, started.ReversalId.Value));
    }

    /// <summary>
    /// <b>伝票の写しが 1 件に当たらなければ止める。</b> 明細側と同じ理由——
    /// 黙って 0 件で通すと、計上済みは不変なので写しが永久に空のまま残る。
    /// </summary>
    [Fact]
    public async Task 伝票の写しが_1_件に当たらなければ止まる()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server, "P900", "株式会社ベガ商会");
        var entry = PostableDraft(server);
        SetEntryPartner(server, entry, partner);

        var draft = await server.EntryStore.LoadAsync(new(entry));

        // 伝票ごと消して、UPDATE が 1 件にも当たらない状況を作る（明細から先に消す）。
        server.Execute($"delete from journal_lines where journal_entry_id = {entry}");
        server.Execute($"delete from journal_entries where id = {entry}");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.SnapshotWriter.BurnAsync(draft));

        Assert.Contains("取引先名の写しを焼けなかった", thrown.Message, StringComparison.Ordinal);
    }
}
