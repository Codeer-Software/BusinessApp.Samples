namespace BusinessApp.AccountingCore.Server.Tests.Journals.Presentation;

using System.Text.Json;
using BusinessApp.AccountingCore.Journals;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.AccountingCore.Shared;
using BusinessApp.AccountingCore.Server.Journals.Application;
using BusinessApp.AccountingCore.Server.Journals.Presentation;

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
    /// <summary>取り消される側の仕訳（借方 現金 1000 / 貸方 未払金 1000）。</summary>
    private static JournalEntryId Original(AccountingServer server)
        => server.InsertPosted(
            1, "5 月分の仕入", "2026-05-20", ("debit", "1100", 1000), ("credit", "2200", 1000));

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

        // **内訳の文言も見る。** 画面はコードで分岐し、文言はそのまま出すので、
        // ここが空になると「何か駄目だった」しか伝わらない。
        Assert.Equal("対象の伝票が指定されていません。", result.Violations[0].Message);
        Assert.Null(result.Violations[0].LineNo);

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

    // --- 権限（qa/03 L-22）---

    /// <summary>
    /// <b>会計の役割を持たない利用者は、取り消すことも訂正することもできない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>この経路には CLB の条件が届かない。</b> モジュールの <c>UserWriteCondition</c> は
    /// <c>ModuleDataIO</c> の保存にしか効かず、ここは <c>IDbAccessor</c> を直に使う（ADR-0016）。
    /// 閉じないと、<c>can_access_app</c> が真の利用者なら誰でも任意の伝票に取消を計上できる——
    /// <b>取消は計上済みで不変（ADR-0004）なので取り返しがつかない</b>。</para>
    /// <para><b>役割の 3 値すべてを撃つ。</b> 「常に真」でも「常に偽」でも緑にならないようにする。</para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("viewer")]
    public async Task 会計の役割が無ければ取り消せない(string? role)
    {
        using var server = new AccountingServer();
        var original = Original(server);
        server.SetAccountingRole(role);
        var before = server.Scalar<long>("select count(*) from journal_entries");

        var result = await server.Amendment.ReverseAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.Equal(JournalAmendmentEndpoint.NotAuthorized, result.Message);
        Assert.Equal([JournalViolationCodes.NotAuthorized], result.Violations.Select(v => v.Code));

        // **伝票が 1 件も増えていない。** 差し戻しの文言だけを見ると、書いてから戻したのか
        // そもそも書かなかったのかが分からない。
        Assert.Equal(before, server.Scalar<long>("select count(*) from journal_entries"));
    }

    /// <summary>訂正も同じ関門で止まる（入口が 1 つなので、経路ごとに開かない）。</summary>
    [Fact]
    public async Task 会計の役割が無ければ訂正できない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        server.SetAccountingRole(null);
        var before = server.Scalar<long>("select count(*) from journal_entries");

        var result = await server.Amendment.CorrectAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.Equal(before, server.Scalar<long>("select count(*) from journal_entries"));
    }

    /// <summary>
    /// <b>調べる操作も閉じる。</b> 伝票が在るか・取り消せるかは会計のデータである。
    /// </summary>
    [Fact]
    public async Task 会計の役割が無ければできることも答えない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        server.SetAccountingRole(null);

        var result = await server.Amendment.AvailabilityAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.False(result.CanReverse);
        Assert.False(result.CanCorrect);
    }

    /// <summary>経理責任者も取り消せる（担当だけに絞っていないこと）。</summary>
    [Fact]
    public async Task 経理責任者は取り消せる()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        server.SetAccountingRole("manager");

        var result = await server.Amendment.ReverseAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, result.Status);
    }

    // --- できること ---

    /// <summary>
    /// できることを返す。<b>そして何も書かない。</b>
    /// </summary>
    /// <remarks>
    /// 画面は<b>伝票を開くたびに</b>これを叩く（`JournalEntry.mod.cs` の
    /// <c>ApplyAmendmentAvailability</c>）。ここが書くようになると、
    /// <b>画面を開いただけで伝票が取り消される</b>。doc コメントに「何も書かない」と
    /// 3 か所で書いてあっても、表明が無ければ何も守っていない。
    /// </remarks>
    [Fact]
    public async Task できることを返すだけで何も書かない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        var before = server.Scalar<long>("select count(*) from journal_entries");

        var result = await server.Amendment.AvailabilityAsync(server.Text(original.Value));

        // **成否ではないので status は ok。** 内容は 2 つの真偽値で表す。
        Assert.Equal(AmendResult.Succeeded, result.Status);
        Assert.True(result.CanReverse);
        Assert.True(result.CanCorrect);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal(0, result.OpenEntryId);

        Assert.Equal(before, server.Scalar<long>("select count(*) from journal_entries"));
    }

    [Fact]
    public async Task 取り消し済みならできないと返す()
    {
        using var server = new AccountingServer();
        // **識別子と伝票番号をずらす**（既定ではどちらも 1 から並び、取り違えを検出できない）。
        server.StartEntryNumbersAt(101);
        var original = Original(server);
        var reversal = await server.Amendment.ReverseAsync(server.Text(original.Value));

        var result = await server.Amendment.AvailabilityAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, result.Status);

        // **2 つを別々に見る。** 今はどちらも false だが、
        // 「片方だけできる状態が将来生まれうるから 2 つに分けた」のが設計意図である。
        Assert.False(result.CanReverse);
        Assert.False(result.CanCorrect);
        Assert.NotEqual(string.Empty, result.Message);

        // **サービスが引いた番号が、この入口を通って出ること。**
        // ここを見ていないと、`AvailabilityAsync` が結果を組み直すときに
        // 番号を落としても全部緑になる（`AmendResult.Available` の注記が警戒している事故）。
        var reversalNo = (await server.EntryStore.LoadAsync(new JournalEntryId(reversal.OpenEntryId))).EntryNo;
        Assert.Equal($"{reversalNo}", result.ReversalEntryNo);
        Assert.Equal(string.Empty, result.CorrectionEntryNo);
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

        // **入口が組み立てた TimeProvider が効いていること。** 計上日は「取り消すと決めた日」で、
        // ここを表明しないと `Create` が渡された時刻を握りつぶしても緑になる。
        // しかも実時刻がたまたま第 18 期に入っているせいで、当分そのまま通ってしまう。
        var reversal = await server.EntryStore.LoadAsync(new JournalEntryId(result.ReversalId));
        Assert.Equal(DateOnly.FromDateTime(AccountingServer.Now.DateTime), reversal.PostingDate);
    }

    /// <summary>
    /// 業務として差し戻したときは<b>巻き戻っている</b>。
    /// </summary>
    /// <remarks>
    /// 差し戻しを戻り値で表すからといって、途中まで書いたものが残ってよいわけではない。
    /// <b>ただしこの経路は 1 行も書く前に止まる</b>ので、巻き戻しそのものを見てはいない。
    /// 書いた後に差し戻す経路は <c>訂正が途中で失敗したら取消だけが残らない</c> が見る。
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

        // 差し戻しでは開く伝票が無い。**0 以外が入ると画面が知らない伝票を開きにいく。**
        Assert.Equal(0, result.OpenEntryId);
        Assert.Equal(0, result.ReversalId);

        Assert.Equal(before, server.Scalar<long>("select count(*) from journal_entries"));
    }

    // --- 複製する（ADR-0048） ---

    /// <summary>
    /// <b>できることに「複製できるか」も返す。</b>
    /// </summary>
    /// <remarks>
    /// <b>複製は原仕訳の状態には依らないが、種別には依る</b>（ADR-0048 の決定 6）。
    /// 返さないと画面は<b>押せるのに必ず断られるボタン</b>を出す（docs/21 §1。2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 複製できるかも返す()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var available = await server.Amendment.AvailabilityAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, available.Status);
        Assert.True(available.CanDuplicate);
        Assert.True(available.CanReverse);
    }

    /// <summary>
    /// <b>取消伝票は、取り消せない・訂正できない・複製もできない。</b>
    /// </summary>
    /// <remarks>
    /// <para>元にできる種別は取消・訂正の対象にできる種別と同じ（ADR-0048 の決定 6。2026-09-10 に
    /// 取消を外した）。画面は「複製する」を出さない。</para>
    /// <para><b>「取消伝票」と「取消済みの原仕訳」は別物</b>で、後者は複製できる
    /// （下の <see cref="取消済みの原仕訳は複製だけできる"/>）。2 本を並べるのは、
    /// 取り違えたまま集合を広げた実例があるから（qa/03 L-40）。</para>
    /// </remarks>
    [Fact]
    public async Task 取消伝票は複製もできない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        var reversalId = (await server.Amendment.ReverseAsync(server.Text(original.Value))).OpenEntryId;

        var available = await server.Amendment.AvailabilityAsync(server.Text(reversalId));

        Assert.Equal(AmendResult.Succeeded, available.Status);
        Assert.False(available.CanDuplicate);
        Assert.False(available.CanReverse);
        Assert.False(available.CanCorrect);

        // **理由の全文を表明する**（qa/03 L-39 の処方）。画面はこれを合計の行に出す。
        Assert.Equal(
            "種別が「取消」の伝票は対象にできません。対象にできるのは通常の伝票と訂正だけです。",
            available.Message);
    }

    /// <summary>
    /// <b>取り消された原仕訳は、取り消せない・訂正できないが、複製はできる。</b>
    /// </summary>
    /// <remarks>
    /// <b>3 つの可否が同じ値で動かないことを見る</b>（縮退。qa/03 L-02）。
    /// 訂正の下書きを消したあとの作り直しが、この場面である（ADR-0048 の状況）。
    /// </remarks>
    [Fact]
    public async Task 取消済みの原仕訳は複製だけできる()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.Amendment.ReverseAsync(server.Text(original.Value));

        var available = await server.Amendment.AvailabilityAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, available.Status);
        Assert.True(available.CanDuplicate);
        Assert.False(available.CanReverse);
        Assert.False(available.CanCorrect);
    }

    /// <summary>複製できない種別は、可否でも false を返す（画面がボタンを出さない）。</summary>
    [Fact]
    public async Task 決算振替は複製もできないと返す()
    {
        using var server = new AccountingServer();
        server.Execute("""
            insert into journal_entries
                (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            values (1, '2026-05-20', '2026-05-20', 'draft', 'closing', '決算振替', '2026-05-20 10:00:00')
            """);
        var id = server.Scalar<long>("select max(id) from journal_entries");

        var available = await server.Amendment.AvailabilityAsync(server.Text(id));

        // **「答えが false」と「呼び出しが失敗」を分ける**（qa/03 L-03）。
        // `CanDuplicate` は差し戻しでも既定の false になるので、Status も見る。
        Assert.Equal(AmendResult.Succeeded, available.Status);
        Assert.False(available.CanDuplicate);
        Assert.False(available.CanReverse);

        // 下書きなので、取消・訂正できない理由も返る（画面は計上済みのときだけ出す）。
        Assert.Equal(
            "この伝票はまだ計上されていません。下書きは削除してください。"
            + "元の伝票を特定できません（保存されていないか、伝票番号がありません）。"
            + "種別が「決算振替」の伝票は対象にできません。対象にできるのは通常の伝票と訂正だけです。",
            available.Message);
    }


    /// <summary>
    /// 複製すると、開くのは<b>新しい下書き</b>で、取消は 1 本も作らない。
    /// </summary>
    /// <remarks>
    /// <b>複製は計上済みを 1 行も動かさない。</b> ここで取消が 1 本でもできていたら、
    /// 「複製したつもりが取り消されていた」という取り返しのつかない失敗になる。
    /// </remarks>
    [Fact]
    public async Task 複製すると新しい下書きが開く()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        var before = server.Scalar<long>("select count(*) from journal_entries");

        var result = await server.Amendment.DuplicateAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, result.Status);
        Assert.Equal("draft", server.StatusOf(new JournalEntryId(result.OpenEntryId)));
        Assert.NotEqual(original.Value, result.OpenEntryId);

        // **取消は作っていない**（<see cref="AmendResult.Opened"/> は 0 を返す）。
        Assert.Equal(0, result.ReversalId);
        Assert.Equal(0, server.CountAmendments(original, "reversal"));

        // 増えたのは 1 本だけ。原仕訳は計上済みのまま。
        Assert.Equal(before + 1, server.Scalar<long>("select count(*) from journal_entries"));
        Assert.Equal("posted", server.StatusOf(original));
    }

    /// <summary>
    /// 取り消された<b>原仕訳</b>も複製できる（<b>新しい記帳だから</b>。ADR-0048 の決定 6）。
    /// </summary>
    /// <remarks>
    /// <b>訂正の下書きを消したあとの受け皿がこれである</b>（qa/04 の 2026-09-04）。
    /// ここを取消・訂正と同じ条件で閉じると、いちばん要る場面で使えない。
    /// </remarks>
    [Fact]
    public async Task 取り消された原仕訳も複製できる()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        await server.Amendment.ReverseAsync(server.Text(original.Value));

        var result = await server.Amendment.DuplicateAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.Succeeded, result.Status);
        Assert.Equal("draft", server.StatusOf(new JournalEntryId(result.OpenEntryId)));

        // **できるのは通常の伝票**（取り消されていたことは写らない）。
        Assert.Equal(
            "normal",
            server.Scalar<string>($"select entry_type from journal_entries where id = {result.OpenEntryId}"));
    }

    /// <summary>
    /// <b>2 回押せば下書きが 2 本できる。</b>
    /// </summary>
    /// <remarks>
    /// <b>冪等ではない</b>（複製に冪等キーは無い。ADR-0048 の決定 2）。
    /// 確認ダイアログを出さないので<b>連打を抑えるものが 1 つも無い</b>——
    /// 取消・訂正では確認が事実上の二重送信よけになっていた（2026-09-09 の自己レビュー）。
    /// <b>意図としてここに固定する</b>：できるのは下書きなので、要らないほうは削除すればよい。
    /// </remarks>
    [Fact]
    public async Task 続けて2回複製すれば下書きが2本できる()
    {
        using var server = new AccountingServer();
        var original = Original(server);

        var first = await server.Amendment.DuplicateAsync(server.Text(original.Value));
        var second = await server.Amendment.DuplicateAsync(server.Text(original.Value));

        Assert.NotEqual(first.OpenEntryId, second.OpenEntryId);
        Assert.Equal(2, server.Scalar<long>("select count(*) from journal_entries where status = 'draft'"));
    }

    /// <summary>会計の役割が無ければ複製もできない（入口が 1 つなので、経路ごとに開かない）。</summary>
    [Fact]
    public async Task 会計の役割が無ければ複製できない()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        server.SetAccountingRole(null);

        var result = await server.Amendment.DuplicateAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.Equal(JournalAmendmentEndpoint.NotAuthorized, result.Message);
    }

    /// <summary>複製の差し戻しは「複製できません」で始まる（押したボタンの言葉。qa/02 R24-23）。</summary>
    [Fact]
    public async Task 会計期間が無ければ複製できませんと断る()
    {
        using var server = new AccountingServer();
        var original = Original(server);
        server.Execute("delete from accounting_periods");

        var result = await server.Amendment.DuplicateAsync(server.Text(original.Value));

        Assert.Equal(AmendResult.RejectedStatus, result.Status);
        Assert.Equal(
            $"複製できません。今日（2026/08/24）に対応する会計期間がありません。", result.Message);
        Assert.Equal([JournalViolationCodes.PeriodNotFound], result.Violations.Select(v => v.Code));
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

        // **落とす地点を「何回目か」で決めない。** 実装が変わって落下点が前へずれると、
        // この検査は「何も書く前に止まった」ことしか見ていない状態へ静かに退化し、
        // **緑のまま無意味**になる（2 回目 → 1 回目に変えても緑だった。2026-08-26 の自己レビュー）。
        // 取消を計上し**終えた**ことを見てから落とし、そこで落ちたことを最後に表明する。
        var reversalPosted = false;
        var droppedAfterPosting = false;
        server.FailBeforeStatement = sql =>
        {
            if (sql.Contains("set status = 'posted'", StringComparison.OrdinalIgnoreCase))
            {
                reversalPosted = true;
            }

            if (reversalPosted && sql.Contains("insert into journal_entries", StringComparison.OrdinalIgnoreCase))
            {
                droppedAfterPosting = true;
                return new InvalidOperationException("再計上の下書きを書く直前で落とす");
            }

            return null;
        };

        // 想定外の失敗なので、業務の差し戻しではなくそのまま投げ直す（ADR-0016）。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Amendment.CorrectAsync(server.Text(original.Value)));

        server.FailBeforeStatement = null;

        Assert.True(
            droppedAfterPosting,
            "取消を計上し終える前に落ちている。この検査は意図した地点を見ていない。");

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
            ["status", "openEntryId", "reversalId", "message", "violations", "canReverse", "canCorrect",
             "canDuplicate", "reversalEntryNo", "correctionEntryNo"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["code", "message", "lineNo"],
            root.GetProperty("violations")[0].EnumerateObject().Select(property => property.Name));

        // **値もリテラルで固定する。** 定数どうしを比べると、値を書き換えても緑のまま通る。
        // 画面は `"ok"` を**リテラルで**比較しているので（JournalEntry.mod.cs）、
        // ここを変えると成功しているのにエラーのトーストが出て、しかも message は空文字なので
        // **空のトーストが出るだけ**になる（qa/01 K-03 の形）。
        Assert.Equal("rejected", root.GetProperty("status").GetString());
        Assert.Equal("E-01", root.GetProperty("violations")[0].GetProperty("code").GetString());
        Assert.Equal(2, root.GetProperty("violations")[0].GetProperty("lineNo").GetInt32());

        using var succeeded = JsonDocument.Parse(JsonSerializer.Serialize(AmendResult.Ok(1, 2)));
        Assert.Equal("ok", succeeded.RootElement.GetProperty("status").GetString());

        // 成功したときは文言も内訳も空。**何か入っていると画面がエラーとして出しかねない。**
        Assert.Equal(string.Empty, succeeded.RootElement.GetProperty("message").GetString());
        Assert.Empty(succeeded.RootElement.GetProperty("violations").EnumerateArray());
        Assert.Equal(2, succeeded.RootElement.GetProperty("openEntryId").GetInt64());
        Assert.Equal(1, succeeded.RootElement.GetProperty("reversalId").GetInt64());

        using var available = JsonDocument.Parse(JsonSerializer.Serialize(
            AmendResult.Available(new AmendmentAvailability(true, false, "理由", 12, null))));
        Assert.Equal("ok", available.RootElement.GetProperty("status").GetString());
        Assert.True(available.RootElement.GetProperty("canReverse").GetBoolean());
        Assert.False(available.RootElement.GetProperty("canCorrect").GetBoolean());

        // **無いことは空文字で表す**（画面が 1 つの見方で判定できるように）。
        Assert.Equal("12", available.RootElement.GetProperty("reversalEntryNo").GetString());
        Assert.Equal(string.Empty, available.RootElement.GetProperty("correctionEntryNo").GetString());
    }

    /// <summary>
    /// ドメインの違反を、<b>3 項目とも</b>写す。
    /// </summary>
    /// <remarks>
    /// <c>LineNo</c> を捨てても、以前は全件緑だった——<c>From</c> を通る唯一の経路が
    /// 行番号なしの違反しか流していなかったため。<b>画面が明細行を指すための情報が黙って落ちる。</b>
    /// </remarks>
    [Fact]
    public void 違反は行番号まで写す()
    {
        var written = AmendViolation.From(new Violation("E-99", "2 行目が変です。", LineNo: 3));

        Assert.Equal("E-99", written.Code);
        Assert.Equal("2 行目が変です。", written.Message);
        Assert.Equal(3, written.LineNo);

        Assert.Null(AmendViolation.From(new Violation("E-98", "伝票全体の話です。")).LineNo);
        Assert.Throws<ArgumentNullException>(() => AmendViolation.From(null!));
    }

    /// <summary>
    /// 要求の項目名。
    /// </summary>
    /// <remarks>
    /// <b>画面が実際に送るのは先頭大文字の <c>OriginalEntryId</c></b>
    /// （`JournalEntry.mod.cs` の <c>body.OriginalEntryId</c>）で、通っているのは
    /// ASP.NET Core の既定が大文字小文字を無視するからである。
    /// <b>実物と、属性で決めた形の両方</b>を通す。
    /// </remarks>
    [Theory]
    [InlineData("""{"OriginalEntryId":"12"}""")]
    [InlineData("""{"originalEntryId":"12"}""")]
    public void 要求の項目名も画面との約束である(string body)
    {
        var request = JsonSerializer.Deserialize<AmendRequest>(
            body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Equal("12", request!.OriginalEntryId);
    }
}
