namespace BusinessApp.Partners.Server.Tests;

using BusinessApp.Partners.Server;
using BusinessApp.Partners.Server.Tests.Fixtures;
using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 登録を保存するときの関門（docs/14 §2）。
/// </summary>
/// <remarks>
/// <b>止めるのは docs/14 §5 の不変条件（書式・付け替え・二重登録・期間・削除・取引先の実在）</b>。
/// ここを通った番号は、計上のときにそのまま明細へ焼き込まれる（計上済みは不変）。
/// </remarks>
public class PartnerRegistrationSubmitGateTests
{
    private const string ValidNo = "T1234567890123";

    private static ModuleSubmitData Adding(params ModuleData[] data)
        => new() { ModuleName = PartnerRegistrationSubmitGate.ModuleName, Add = [.. data] };

    private static ModuleSubmitData Updating(params ModuleData[] data)
        => new() { ModuleName = PartnerRegistrationSubmitGate.ModuleName, Update = [.. data] };

    /// <summary>CLB は<b>変更されたフィールドしか送ってこない</b>ので、渡された項目だけ載せる。</summary>
    /// <remarks><c>clearEndedOn</c> / <c>clearEndReason</c> は「欄を空にする変更」——
    /// フィールドは差分に載るが値が空、という形を組む（触っていない＝載らない、とは別）。</remarks>
    private static ModuleData Registration(
        string? no = null, long? partnerId = null, DateOnly? validFrom = null, long? id = null,
        DateOnly? endedOn = null, string? endReason = null,
        bool clearEndedOn = false, bool clearEndReason = false)
    {
        var data = new ModuleData { Name = PartnerRegistrationSubmitGate.ModuleName };

        if (id is long rowId)
        {
            data.Fields["Id"] = new IdFieldData
            {
                Value = rowId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        if (no is not null)
        {
            data.Fields["RegistrationNo"] = new TextFieldData { Value = no };
        }

        if (partnerId is long partner)
        {
            // **画面が送ってくるのは識別子フィールドである**（2026-08-31 に移設）。
            // 登録の入力は取引先の詳細に置いてあり、親 FK は IdFieldDesign でなければ
            // 追加が静かに消える（qa/01 D-17）。フィクスチャが実際と違う型を組むと、
            // 関門が素通しに落ちてもテストが緑のままになる（R28-07 で踏んだ型）。
            // ModuleFieldData.Id と取り違えると、関門はいつも null を見て素通しする。
            data.Fields["Partner"] = new IdFieldData
            {
                Value = partner.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        if (validFrom is DateOnly from)
        {
            data.Fields["ValidFrom"] = new DateFieldData { Value = from };
        }

        if (endedOn is not null || clearEndedOn)
        {
            data.Fields["EndedOn"] = new DateFieldData { Value = endedOn };
        }

        if (endReason is not null || clearEndReason)
        {
            data.Fields["EndReason"] = new SelectFieldData { Value = endReason };
        }

        return data;
    }

    private static long InsertPartner(PartnerServer server, string code = "P900")
    {
        server.Execute($"insert into partners (code, name, is_active) values ('{code}', '取引先 {code}', 1)");
        return server.Scalar<long>("select last_insert_rowid()");
    }

    private static long InsertRegistration(
        PartnerServer server, long partnerId, string no, string validFrom,
        string? endedOn = null, string reason = "revoked")
    {
        if (endedOn is null)
        {
            server.Execute($"""
                insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
                values ({partnerId}, '{no}', {PartnerServer.DateLiteral(validFrom)})
                """);
        }
        else
        {
            server.Execute($"""
                insert into partner_invoice_registrations
                    (partner_id, registration_no, valid_from, ended_on, end_reason)
                values ({partnerId}, '{no}', {PartnerServer.DateLiteral(validFrom)},
                        {PartnerServer.DateLiteral(endedOn)}, '{reason}')
                """);
        }

        return server.Scalar<long>("select last_insert_rowid()");
    }

    private static PartnerRegistrationSubmitGate Gate(PartnerServer server) => new(server.Registrations);

    /// <summary>保存が呼ばれたかどうかを見張る。</summary>
    private sealed class SaveSpy
    {
        public bool Called { get; private set; }

        public Task<List<ModuleSubmitResult>> SaveAsync()
        {
            Called = true;
            return Task.FromResult(new List<ModuleSubmitResult>());
        }
    }

    // --- 束ねる（docs/21 §2-6 の (b)） ---------------------------------------------

    /// <summary>断られた保存の文言。保存は呼ばれていないことも見る。</summary>
    private static async Task<string> RejectedMessage(PartnerServer server, params ModuleSubmitData[] data)
    {
        var save = new SaveSpy();
        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(data, save.SaveAsync));
        Assert.False(save.Called);
        return thrown.Message;
    }

    /// <summary>
    /// <b>行の違反を全部 1 度で言い、画面の並び（登録番号 → 登録年月日 → 取消・失効）で並べる。</b>
    /// </summary>
    /// <remarks>
    /// <b>2026-09-24 までは最初の 1 つ（登録番号）で止めていた</b>（(a)）。
    /// <b>終わりと理由の対（R-I1）と、終わりが始まりより前（R-I2）は両方言う</b>——別々の欄の誤りである。
    /// </remarks>
    [Fact]
    public async Task 行の違反を全部一度で言う()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);

        var message = await RejectedMessage(server, Adding(Registration(
            "T123", partner, new DateOnly(2025, 1, 1), endedOn: new DateOnly(2024, 12, 31))));

        Assert.Equal(
            "登録できません（3 件）。"
            + "①「登録番号」の形が違います。登録番号は「T」で始まる 14 桁（T のあとに数字 13 桁）です。入力し直してください。"
            + "②「取消・失効年月日」と「取消・失効の理由」は、両方入力するか、両方空にしてください。"
            + "③「取消・失効年月日」（2024/12/31）が「登録年月日」（2025/01/01）より前になっています。日付を確かめてください。",
            message);
    }

    /// <summary>
    /// <b>同じ日から始まる 2 件（R-I3）に当たった保存では、期間の重なりを言わない。</b>
    /// </summary>
    /// <remarks>
    /// 保存済みの終わりの無い登録（2025/01/01 から）と同じ日から始まる行を足す。期間の検査だけなら
    /// 「終わりの無い登録のあとに入力した」（R-I5）も当たるが、<b>始まりの日が重なっているかぎり、
    /// どちらの終わりを入れても通らない</b>——先に登録年月日を直させる。<b>登録番号の書式は別の欄なので一緒に言う。</b>
    /// </remarks>
    [Fact]
    public async Task 同じ日から始まる二件に当たれば期間は言わない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2025-01-01");

        var message = await RejectedMessage(server, Adding(Registration("T123", partner, new DateOnly(2025, 1, 1))));

        Assert.Equal(
            "登録できません（2 件）。"
            + "①「登録番号」の形が違います。登録番号は「T」で始まる 14 桁（T のあとに数字 13 桁）です。入力し直してください。"
            + "②この取引先には 2025/01/01 から始まる登録が既にあります。入力している登録の登録年月日が国税庁の公表サイトと違うなら「登録年月日」を直し、合っているなら、先にある登録を取引先の詳細の「登録番号の履歴」から直してください。",
            message);
    }

    /// <summary>
    /// <b>同じ日から始まる 2 件に当たっても、行の中だけで決まる規則は言う</b>（終わりと理由の対・R-I1）。
    /// </summary>
    /// <remarks>
    /// 飛ばすのは重なり（R-I4・R-I5）だけ——R-I1 は始まりの日にも、どの取引先かにも依らない。
    /// 飛ばすと、登録年月日を直した 2 回目で初めて「理由を選んで」と言われる（2026-09-24 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 同じ日から始まる二件に当たっても行の中の規則は言う()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2025-01-01");

        var message = await RejectedMessage(server, Adding(Registration(
            ValidNo, partner, new DateOnly(2025, 1, 1), endedOn: new DateOnly(2025, 6, 30))));

        Assert.Equal(
            "登録できません（2 件）。"
            + "①この取引先には 2025/01/01 から始まる登録が既にあります。入力している登録の登録年月日が国税庁の公表サイトと違うなら「登録年月日」を直し、合っているなら、先にある登録を取引先の詳細の「登録番号の履歴」から直してください。"
            + "②「取消・失効年月日」と「取消・失効の理由」は、両方入力するか、両方空にしてください。",
            message);
    }

    /// <summary>
    /// <b>付け替えを断った行は、同じ日から始まる 2 件に数えない</b>——移れない先の取引先の話になる。
    /// </summary>
    /// <remarks>
    /// A の行を B へ付け替え、B の保存済みの行と同じ登録年月日にする（API の経路）。同じ日の 2 件の検査だけなら
    /// 「この取引先には 2025/01/01 から始まる登録が既にあります」も当たるが、それは移れない B の話である
    /// （(a) では付け替えの断りが先に投げていたので、暗黙に守られていた——2026-09-24 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 付け替えを断った行は同じ日の二件に数えない()
    {
        using var server = new PartnerServer();
        var a = InsertPartner(server, "P861");
        var b = InsertPartner(server, "P862");
        var row = InsertRegistration(server, a, ValidNo, "2024-01-01");
        InsertRegistration(server, b, "T9999999999999", "2025-01-01");

        var message = await RejectedMessage(server, Updating(Registration(
            partnerId: b, validFrom: new DateOnly(2025, 1, 1), id: row)));

        Assert.Equal(
            "登録できません。登録の「取引先」は、保存したあとは変更できません。別の取引先の登録にするときは、その取引先の画面で入力し直してください。",
            message);
    }

    /// <summary>
    /// <b>同じ日の 2 件に当たった取引先だけ、重なりを見ない</b>——別の取引先の重なりは言う。
    /// </summary>
    /// <remarks>
    /// 取引先 1 には保存済みの終わりの無い行（2025/01/01 から）と同じ日の行を足し、取引先 2 には保存済みの終わりの無い行
    /// （2024/01/01 から）のあとに終わりの無い行を足す（API・取込の経路——画面は 1 行ずつ保存する）。
    /// </remarks>
    [Fact]
    public async Task 同じ日の二件に当たった取引先だけ重なりを見ない()
    {
        using var server = new PartnerServer();
        var first = InsertPartner(server, "P871");
        var second = InsertPartner(server, "P872");
        InsertRegistration(server, first, ValidNo, "2025-01-01");
        InsertRegistration(server, second, ValidNo, "2024-01-01");

        var message = await RejectedMessage(server, Adding(
            Registration(ValidNo, first, new DateOnly(2025, 1, 1)),
            Registration(ValidNo, second, new DateOnly(2025, 1, 1))));

        Assert.Equal(
            "登録できません（2 件）。"
            + "①この取引先には 2025/01/01 から始まる登録が既にあります。入力している登録の登録年月日が国税庁の公表サイトと違うなら「登録年月日」を直し、合っているなら、先にある登録を取引先の詳細の「登録番号の履歴」から直してください。"
            + "②登録の期間が重なっています。この取引先には「取消・失効年月日」が空の登録（2024/01/01 から）があり、"
            + "入力している登録（2025/01/01 から）はそのあとに始まります。"
            + "国税庁の公表サイトで、2024/01/01 からの登録の取消年月日か失効年月日が 2025/01/01 以前なら、"
            + "先に取引先の詳細の「登録番号の履歴」でその行の「編集」を開き、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            message);
    }

    /// <summary>
    /// <b>取引先が見つからなくても、行の中だけで決まる規則は言う</b>（R-I1 は取引先に依らない）。
    /// </summary>
    [Fact]
    public async Task 取引先が見つからなくても行の中の規則は言う()
    {
        using var server = new PartnerServer();

        var message = await RejectedMessage(server, Adding(Registration(
            ValidNo, 9999, new DateOnly(2025, 1, 1), endedOn: new DateOnly(2025, 6, 30))));

        Assert.Equal(
            "登録できません（2 件）。"
            + "①取引先が見つかりません。取引先の詳細の「登録番号を追加する」から入り直してください。"
            + "②「取消・失効年月日」と「取消・失効の理由」は、両方入力するか、両方空にしてください。",
            message);
    }

    /// <summary>
    /// <b>行そのものが崩れていれば、期間の重なりを言わない。</b>
    /// </summary>
    /// <remarks>
    /// 足す行は終わりが始まりより前（R-I2）。保存済みの終わりの無い登録（2024/06/01 から）のあとに始まるので、
    /// 期間の検査だけなら R-I5 も当たるが、<b>崩れた日付の上で言う重なりは従いようがない</b>。
    /// </remarks>
    [Fact]
    public async Task 崩れた行があれば重なりは言わない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2024-06-01");

        var message = await RejectedMessage(server, Adding(Registration(
            ValidNo, partner, new DateOnly(2025, 1, 1), endedOn: new DateOnly(2024, 12, 31), endReason: "revoked")));

        Assert.Equal(
            "登録できません。「取消・失効年月日」（2024/12/31）が「登録年月日」（2025/01/01）より前になっています。日付を確かめてください。",
            message);
    }

    /// <summary>
    /// <b>別の早い行の重なりは別に言う。同じ早い行については、いちばん近いあとの行との組だけを言う。</b>
    /// </summary>
    /// <remarks>
    /// <para>保存済み: C（2026/01/01 から、終わり無し）。同じ保存で足す: A（2024/01/01 から、終わり無し）と
    /// B（2025/01/01〜2026/06/30）。</para>
    /// <para>A は B の前で終わっていない（R-I5）、B は C と重なる（R-I4）——<b>直す先が 2 つある</b>。
    /// A は C の前でも終わっていないが、<b>B までに終われば C の前でも終わる</b>ので言わない。</para>
    /// <para>画面は 1 行ずつ保存する（docs/14 §5）ので、2 行が一度に来るのは API と取込（フェーズ 6）だけである。</para>
    /// </remarks>
    [Fact]
    public async Task 別の早い行の重なりは別に言い同じ早い行は一つだけ言う()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2026-01-01");

        var message = await RejectedMessage(server, Adding(
            Registration(ValidNo, partner, new DateOnly(2024, 1, 1)),
            Registration(ValidNo, partner, new DateOnly(2025, 1, 1), endedOn: new DateOnly(2026, 6, 30), endReason: "revoked")));

        Assert.Equal(
            "登録できません（2 件）。"
            + "①登録の期間が重なっています。入力している登録（2024/01/01 から）は「取消・失効年月日」が空ですが、"
            + "そのあとに 2025/01/01 からの登録があります。"
            + "国税庁の公表サイトで、入力している登録の取消年月日か失効年月日が 2025/01/01 以前なら、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。"
            + "②登録の期間が重なっています。2025/01/01 からの登録の「取消・失効年月日」（2026/06/30）が、"
            + "次の登録の「登録年月日」（2026/01/01）より後になっています。「登録年月日」と「取消・失効年月日」を確かめてください。",
            message);
    }

    [Fact]
    public async Task 正しい登録は通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(ValidNo, partner, new DateOnly(2023, 10, 1)))], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Theory]
    [InlineData("1234567890123")]
    [InlineData("t1234567890123")]
    [InlineData("T123456789012")]
    [InlineData("T123456789012X")]
    [InlineData("")]
    public async Task 書式の違う登録番号を止める(string no)
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no, partner, new DateOnly(2023, 10, 1)))], save.SaveAsync));

        // 全文で固める（qa/03 L-66）。改行が無いこと（トーストは改行できない。qa/01 D-12）も、全文一致が兼ねる
        Assert.Equal("登録できません。「登録番号」の形が違います。登録番号は「T」で始まる 14 桁（T のあとに数字 13 桁）です。入力し直してください。", thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>更新も見る。</b> 正しい番号で作ってから壊した番号に直せるなら、関門は無いのと同じである。
    /// </summary>
    [Fact]
    public async Task 更新で壊した登録番号も止める()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync([Updating(Registration("T12"))], save.SaveAsync));

        Assert.Equal("登録できません。「登録番号」の形が違います。登録番号は「T」で始まる 14 桁（T のあとに数字 13 桁）です。入力し直してください。", thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// 登録番号が差分に載っていない保存（他の項目だけ直した）は、番号を検査しない。
    /// <b>送られていない項目は「変えていない」である</b>（qa/01 F-11）。
    /// </summary>
    [Fact]
    public async Task 登録番号を触らない更新は通す()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Updating(Registration())], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Fact]
    public async Task 同じ取引先の同じ日から始まる登録を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, 'T1111111111111', {PartnerServer.DateLiteral("2023-10-01")})
            """);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(ValidNo, partner, new DateOnly(2023, 10, 1)))], save.SaveAsync));

        // **全文で見る**——2 つの手を並べる断りは、選ぶ目安まで言っているかを見る（docs/21 §2-3。2026-09-24 の自己レビュー）。
        Assert.Equal(
            "登録できません。この取引先には 2023/10/01 から始まる登録が既にあります。"
            + "入力している登録の登録年月日が国税庁の公表サイトと違うなら「登録年月日」を直し、"
            + "合っているなら、先にある登録を取引先の詳細の「登録番号の履歴」から直してください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>自分自身は二重登録に数えない。</b> 数えると、登録年月日を変えない更新が全部止まる。
    /// </summary>
    [Fact]
    public async Task 同じ登録を直す更新は通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, '{ValidNo}', {PartnerServer.DateLiteral("2023-10-01")})
            """);
        var row = server.Scalar<long>("select last_insert_rowid()");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Registration(ValidNo, partner, new DateOnly(2023, 10, 1), row))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>登録番号を差分に載せずに、登録年月日だけ他の行の日へ動かす更新も止める。</b>
    /// 番号で自分自身を見分けていたときは、この形が判定できなかった。
    /// </summary>
    [Fact]
    public async Task 登録年月日だけを他の行の日へ動かす更新を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, "T1111111111111", "2023-10-01", "2026-03-31");
        var moving = InsertRegistration(server, partner, ValidNo, "2026-04-01");
        var save = new SaveSpy();

        // **取引先は差分に載せない。** CLB は変えたフィールドしか送ってこないので、
        // 画面で登録年月日だけを直すとこの形になる。
        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(validFrom: new DateOnly(2023, 10, 1), id: moving))],
                save.SaveAsync));

        Assert.False(save.Called);
    }

    /// <summary>
    /// 行の識別子も取引先も差分に無ければ、二重登録は判定しない（判定材料が無い）。
    /// </summary>
    [Fact]
    public async Task 行を特定できなければ二重登録は見ない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, 'T1111111111111', {PartnerServer.DateLiteral("2023-10-01")})
            """);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Registration(validFrom: new DateOnly(2023, 10, 1)))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>貼り付けで紛れ込んだ空白は落として保存する。</b>
    /// 落とさないと、同じ番号が 2 通りの文字列で保存されて突合が壊れる。
    /// </summary>
    [Fact]
    public async Task 前後の空白を落として保存する()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var data = Registration($"  {ValidNo} ", partner, new DateOnly(2023, 10, 1));
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(data)], save.SaveAsync);

        Assert.True(save.Called);
        Assert.Equal(ValidNo, (data.Fields["RegistrationNo"] as TextFieldData)?.Value);
    }

    [Fact]
    public async Task 日の違う登録は通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from, ended_on, end_reason)
            values ({partner}, 'T1111111111111', {PartnerServer.DateLiteral("2023-10-01")}, {PartnerServer.DateLiteral("2026-03-31")}, 'revoked')
            """);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(ValidNo, partner, new DateOnly(2026, 4, 1)))], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Fact]
    public async Task 別の取引先の同じ日は止めない()
    {
        using var server = new PartnerServer();
        var owner = InsertPartner(server, "P900");
        var other = InsertPartner(server, "P901");
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({owner}, 'T1111111111111', {PartnerServer.DateLiteral("2023-10-01")})
            """);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(ValidNo, other, new DateOnly(2023, 10, 1)))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>登録年月日が差分に無ければ、二重登録は判定しない（動かしていないので起きない）。</summary>
    [Fact]
    public async Task 登録年月日が差分に無ければ二重登録は見ない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, 'T1111111111111', {PartnerServer.DateLiteral("2023-10-01")})
            """);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Updating(Registration(ValidNo, partner))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>入れ物の名前ではなく、中身の名前で担当を決める。</b>
    /// </summary>
    /// <remarks>
    /// <para>入れ物（<see cref="ModuleSubmitData.ModuleName"/>）が別モジュールでも、
    /// <b>中に登録が混ざっていれば見る</b>。<c>ModuleName</c> で絞る実装に変えても
    /// 「素通しする」側のテストは通ってしまうので、こちら側から挟む
    /// （2026-08-27 の自己レビュー R16-16。取引先側の同名テストにだけあった穴）。</para>
    /// <para><b>取引先の詳細に登録を置いた以上、この形は実際に来る</b>——
    /// 親が <c>Partner</c>、子が <c>PartnerInvoiceRegistration</c> の 1 回の保存になる。</para>
    /// </remarks>
    [Fact]
    public async Task 入れ物が別モジュールでも中の登録は見る()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);

        var submit = new ModuleSubmitData
        {
            ModuleName = ForeignModuleData.JournalEntryModuleName,
            Add = [ForeignModuleData.Entry("1"), Registration(no: "1234567890123", partnerId: partner)],
        };

        var save = new SaveSpy();
        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync([submit], save.SaveAsync));
        Assert.False(save.Called);
    }

    /// <summary>関門は<b>自分のモジュールだけ</b>を見る。仕訳の保存に割り込まない。</summary>
    [Fact]
    public async Task 別のモジュールの保存は素通しする()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([ForeignModuleData.Adding(ForeignModuleData.Entry("1"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    // --- 同じ保存の中の二重登録（登録の入力を取引先の詳細に置いたので実際に起こる。docs/14 §4）---

    /// <summary>
    /// <b>同じ保存に、同じ取引先の同じ日から始まる登録が 2 件</b>。どちらも DB にまだ無い。
    /// </summary>
    /// <remarks>
    /// 保存済みの行としか突き合わせない実装だと、片方ずつ見て両方が通る。
    /// <b>DB も止められない</b>——<c>UNIQUE</c> は登録番号まで含むので、番号が違えば入る。
    /// </remarks>
    [Fact]
    public async Task 同じ保存に同じ日から始まる登録が二件あれば止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var submit = Adding(
            Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1)),
            Registration(no: "T9999999999999", partnerId: partner, validFrom: new DateOnly(2023, 10, 1)));

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync([submit], save.SaveAsync));

        // **全文で見る**——同じ日の 2 件に当たった取引先は重なりを見ない（どちらも終わりが無いので、重なりの検査だけなら R-I5 も当たる）。
        Assert.Equal(
            "登録できません。この取引先には 2023/10/01 から始まる登録を 2 件入力しています。どちらかの「登録年月日」を直してください。",
            thrown.Message);
        Assert.False(save.Called);
        Assert.Equal(0L, server.Scalar<long>("select count(*) from partner_invoice_registrations"));
    }

    /// <summary>
    /// <b>取引先も同じ保存で作られる場合</b>（仮の識別子）でも、同じ日の 2 件を止める。
    /// </summary>
    /// <remarks>
    /// 取引先の詳細に登録を置いたので、<b>取引先ごと新規作成する経路が生まれた</b>。
    /// 仮の識別子は数値として読めないので、<b>文字列のまま突き合わせる</b>（関門の注記）。
    /// </remarks>
    [Fact]
    public async Task 取引先が新規でも同じ日から始まる登録の二件目を止める()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var first = Registration(no: ValidNo, validFrom: new DateOnly(2023, 10, 1));
        var second = Registration(no: "T9999999999999", validFrom: new DateOnly(2023, 10, 1));
        first.Fields["Partner"] = new IdFieldData { Value = "@temporary:aaaa-bbbb" };
        second.Fields["Partner"] = new IdFieldData { Value = "@temporary:aaaa-bbbb" };

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync([Adding(first, second)], save.SaveAsync));
        Assert.False(save.Called);
    }

    /// <summary>相手が違えば、同じ日から始まっていても通る。</summary>
    [Fact]
    public async Task 取引先が違えば同じ日から始まる登録を通す()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var first = Registration(no: ValidNo, validFrom: new DateOnly(2023, 10, 1));
        var second = Registration(no: "T9999999999999", validFrom: new DateOnly(2023, 10, 1));
        first.Fields["Partner"] = new IdFieldData { Value = "@temporary:aaaa" };
        second.Fields["Partner"] = new IdFieldData { Value = "@temporary:bbbb" };

        await Gate(server).SubmitAsync([Adding(first, second)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>同じ相手でも、始まる日が違えば通す（登録 → 取消 → 再登録の履歴）。</summary>
    /// <remarks>先の登録は閉じておく——終わりのない行のあとに行は作れない（docs/14 §5 R-I5）。</remarks>
    [Fact]
    public async Task 同じ相手でも始まる日が違えば通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(
                Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1),
                    endedOn: new DateOnly(2025, 3, 31), endReason: "revoked"),
                Registration(no: "T9999999999999", partnerId: partner, validFrom: new DateOnly(2025, 4, 1)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>登録年月日が差分に無い行</b>は、この検査の対象にならない。
    /// </summary>
    /// <remarks>
    /// CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。公表名だけを直した保存で
    /// 「相手が同じだから 2 件目」と数えると、直せない画面ができる。
    /// </remarks>
    [Fact]
    public async Task 登録年月日が差分に無い行は数えない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(
                Registration(partnerId: partner, id: 1),
                Registration(partnerId: partner, id: 2))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>取引先が差分に無い行</b>は、保存済みの値から引いて突き合わせる。
    /// </summary>
    /// <remarks>
    /// 画面で登録年月日だけを直した 2 行が、同じ相手の同じ日に揃うことがある。
    /// 差分に無いからと諦めると素通りする。
    /// </remarks>
    [Fact]
    public async Task 取引先が差分に無くても保存済みの値で突き合わせる()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var first = InsertRegistration(server, partner, ValidNo, "2023-10-01", "2025-03-31");
        var second = InsertRegistration(server, partner, "T9999999999999", "2025-04-01");
        var save = new SaveSpy();

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(
                    Registration(validFrom: new DateOnly(2026, 1, 1), id: first),
                    Registration(validFrom: new DateOnly(2026, 1, 1), id: second))],
                save.SaveAsync));
        Assert.False(save.Called);
    }

    // --- 取引先の付け替え（docs/14 §4）---

    /// <summary>
    /// <b>既にある登録の取引先を、別の相手へ付け替える保存を止める。</b>
    /// </summary>
    /// <remarks>
    /// 2026-08-31 まで止めていたのは画面側の <c>IsUpdateProtected</c> で、
    /// 入力を取引先の詳細へ移したときに一緒に外れていた（自己レビューで発見）。
    /// <b>画面の形は守りではない</b>ので、関門に移した。
    /// </remarks>
    [Fact]
    public async Task 既にある登録の取引先を付け替える保存を止める()
    {
        using var server = new PartnerServer();
        var owner = InsertPartner(server, "P901");
        var other = InsertPartner(server, "P902");
        var row = InsertRegistration(server, owner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(partnerId: other, id: row))], save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の「取引先」は、保存したあとは変更できません。別の取引先の登録にするときは、その取引先の画面で入力し直してください。",
            thrown.Message);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
        Assert.Equal(owner, server.Scalar<long>(
            $"select partner_id from partner_invoice_registrations where id = {row}"));
    }

    /// <summary>同じ相手を送り直すだけの保存は通す（親が FK を毎回載せてくる経路）。</summary>
    [Fact]
    public async Task 同じ取引先を送り直す保存は通す()
    {
        using var server = new PartnerServer();
        var owner = InsertPartner(server, "P901");
        var row = InsertRegistration(server, owner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        // **先頭 0 付きで送っても同じ相手と見なす**（鍵の正規化。Key）。
        var data = Registration(id: row);
        data.Fields["Partner"] = new IdFieldData
        {
            Value = "00" + owner.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await Gate(server).SubmitAsync([Updating(data)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>取引先が差分に無い保存は、付け替えの検査をしない。</b>
    /// CLB は変更されたフィールドしか送ってこない（qa/01 F-11）。
    /// </summary>
    [Fact]
    public async Task 取引先を触らない更新は付け替えとみなさない()
    {
        using var server = new PartnerServer();
        var owner = InsertPartner(server, "P901");
        var row = InsertRegistration(server, owner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Registration(no: "T9999999999999", id: row))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>新規は付け替えではない。</b> 仮の識別子で来るので、保存済みの行と比べようがない。
    /// </summary>
    [Fact]
    public async Task 新規の登録は付け替えの検査に掛からない()
    {
        using var server = new PartnerServer();
        var owner = InsertPartner(server, "P901");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(no: ValidNo, partnerId: owner, validFrom: new DateOnly(2023, 10, 1)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// 保存済みの行が引けないとき（消えている）は、この関門は黙って通す。
    /// その保存は外部キーで別に失敗するので、ここで違う言葉を被せない。
    /// </summary>
    [Fact]
    public async Task 保存済みの行が無い更新は付け替えとみなさない()
    {
        using var server = new PartnerServer();
        var owner = InsertPartner(server, "P901");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Registration(partnerId: owner, id: 900))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>親 FK が参照フィールドで来ても、二重登録を止める。</b>
    /// </summary>
    /// <remarks>
    /// 画面が送ってくるのは識別子フィールドだが（移設後の正典。qa/01 D-17）、
    /// <b>関門は型を 1 つに決め打ちしない</b>。取込（フェーズ 6）も API を直に叩く経路も
    /// 同じ入口を通るからで、どちらか片方しか読めない関門は、型が動いた日に
    /// <b>丸ごと素通しに落ちる——しかもフィクスチャが型を自分で組むのでテストは緑のまま</b>
    /// である（2026-08-31 の自己レビュー）。この 1 本が、もう一方の経路を押さえる。
    /// </remarks>
    [Fact]
    public async Task 親FKが参照フィールドでも二重登録を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var row = Registration(no: "T9999999999999", validFrom: new DateOnly(2023, 10, 1));
        row.Fields["Partner"] = new LinkFieldData
        {
            Value = partner.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync([Adding(row)], save.SaveAsync));
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>参照フィールドで来た行と、保存済みから引いた行が、同じ相手だと分かる。</b>
    /// </summary>
    /// <remarks>
    /// 鍵の作り方が 2 通りある（送られてきた文字列と、DB の値から作った文字列）。
    /// 片方が <c>"007"</c>、片方が <c>"7"</c> のように揃わないと、
    /// <b>同じ相手を別物と見なして二重登録が通る</b>。両方の経路が 1 つの保存に混ざる形で固定する。
    /// </remarks>
    [Fact]
    public async Task 送られてきた相手と保存済みの相手を同じ鍵で突き合わせる()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var existing = InsertRegistration(server, partner, ValidNo, "2020-04-01");
        var save = new SaveSpy();

        // 1 件目は保存済みの行を動かす（取引先は差分に無い＝DB から引く）。
        var moved = Registration(validFrom: new DateOnly(2026, 1, 1), id: existing);
        // 2 件目は新規で、取引先を**先頭 0 付き**の文字列で指す。
        var added = Registration(no: "T9999999999999", validFrom: new DateOnly(2026, 1, 1));
        added.Fields["Partner"] = new IdFieldData
        {
            Value = "00" + partner.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                    Add = [added],
                    Update = [moved],
                }],
                save.SaveAsync));
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>相手が違えば、保存済みから引いた行どうしでも通す。</b>
    /// </summary>
    /// <remarks>
    /// 相手を DB から引く経路で「別の相手を同じと見なす」誤りを見る。
    /// 取引先を 1 件しか作らないと、<c>FindPartnerOfAsync</c> が引数を無視して
    /// いつも同じ相手を返す実装でも緑になる（2026-08-31 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 保存済みから引いた相手が違えば同じ日へ動かせる()
    {
        using var server = new PartnerServer();
        var first = InsertPartner(server, "P901");
        var second = InsertPartner(server, "P902");
        var a = InsertRegistration(server, first, ValidNo, "2020-04-01");
        var b = InsertRegistration(server, second, "T9999999999999", "2021-04-01");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(
                Registration(validFrom: new DateOnly(2026, 1, 1), id: a),
                Registration(validFrom: new DateOnly(2026, 1, 1), id: b))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>同じ保存で 2 行の登録年月日を入れ替えられる。</b>
    /// </summary>
    /// <remarks>
    /// 保存済みの値としか突き合わせないと、A の新しい日付が<b>これから動く B の古い日付</b>に
    /// 当たって「既にあります」で誤って止まる（2026-08-31 の自己レビュー）。
    /// <b>この保存で日付が動く行は、保存済みの値で数えない。</b>
    /// </remarks>
    /// <remarks>
    /// <para>期間ごと入れ替える——登録年月日だけ入れ替えると期間が壊れる（docs/14 §5 R-I2）。</para>
    /// <para><b>ここで表明しているのは関門の判定（最終状態が正しければ通す）だけ</b>である。
    /// DB の一意索引・トリガは文単位で検査するので、1 回の保存で実際に入れ替えられるかは
    /// **文の順序に依存する**（docs/14 §5 の帰結）。画面は 1 行ずつ保存するのでこの形は出ない。
    /// 複数行を 1 保存で送る取込（フェーズ 6）の設計時に、実際の保存で確かめる。</para>
    /// </remarks>
    [Fact]
    public async Task 同じ保存で二行の登録年月日を入れ替えられる()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var a = InsertRegistration(server, partner, ValidNo, "2023-10-01", "2024-03-31");
        var b = InsertRegistration(server, partner, "T9999999999999", "2025-04-01", "2026-03-31");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(
                Registration(validFrom: new DateOnly(2025, 4, 1), id: a, endedOn: new DateOnly(2026, 3, 31)),
                Registration(validFrom: new DateOnly(2023, 10, 1), id: b, endedOn: new DateOnly(2024, 3, 31)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    // --- 期間の不変条件（docs/14 §5 R-I1・R-I2・R-I4・R-I5）---

    /// <summary>新規の行に登録年月日が無ければ、言葉で断る（DB の NOT NULL を生で見せない）。</summary>
    [Fact]
    public async Task 登録年月日のない新規を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: ValidNo, partnerId: partner))], save.SaveAsync));

        Assert.Equal("登録できません。「登録年月日」を入力してください。", thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>終わりの日だけで理由が無い行を止める（R-I1）。</summary>
    [Fact]
    public async Task 終わりの日だけで理由のない新規を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1),
                    endedOn: new DateOnly(2026, 3, 31)))],
                save.SaveAsync));

        Assert.Contains("両方入力するか、両方空に", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>理由だけで終わりの日が無い行を止める（R-I1）。</summary>
    [Fact]
    public async Task 理由だけで終わりの日のない新規を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1),
                    endReason: "revoked"))],
                save.SaveAsync));

        Assert.Contains("両方入力するか、両方空に", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>登録より前に終わる行を止める（R-I2。DB の CHECK を生で見せない）。</summary>
    [Fact]
    public async Task 登録より前に終わる新規を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1),
                    endedOn: new DateOnly(2023, 9, 30), endReason: "revoked"))],
                save.SaveAsync));

        Assert.Contains("2023/09/30", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("2023/10/01", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>登録と同じ日に終わる行は通す（同日はあり得るか未確認なので許す——docs/14 §2）。</summary>
    [Fact]
    public async Task 登録と同じ日に終わる新規は通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1),
                endedOn: new DateOnly(2023, 10, 1), endReason: "expired"))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>終わりの日だけ直した更新は、保存済みの登録年月日と突き合わせる。</b>
    /// CLB は変更されたフィールドしか送ってこないので、差分だけ見ると登録年月日が読めない。
    /// </summary>
    [Fact]
    public async Task 終わりの日だけ直した更新も保存済みの登録年月日と突き合わせる()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var row = InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(id: row, endedOn: new DateOnly(2020, 1, 1), endReason: "revoked"))],
                save.SaveAsync));

        Assert.Contains("2020/01/01", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>終わりの日をクリアして理由を残す更新を止める（欄を空にする変更も差分に載る）。</summary>
    [Fact]
    public async Task 終わりの日をクリアして理由を残す更新を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var row = InsertRegistration(server, partner, ValidNo, "2023-10-01", "2026-03-31");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(id: row, clearEndedOn: true))], save.SaveAsync));

        Assert.Contains("両方入力するか、両方空に", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>終わりの日と理由を両方クリアする更新は通す（取消の記録の取り消し）。</summary>
    [Fact]
    public async Task 終わりの日と理由を両方クリアする更新は通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var row = InsertRegistration(server, partner, ValidNo, "2023-10-01", "2026-03-31");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Registration(id: row, clearEndedOn: true, clearEndReason: true))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>前の登録が終わった日に次が始まる再登録（隣接）は通す。</b>
    /// 計上時の引き当ては「終わりの日を含み、同日は新しいほうを採る」（docs/14 §5）。
    /// </summary>
    [Fact]
    public async Task 前の登録が終わった日に始まる再登録は通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01", "2026-03-31");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2026, 3, 31)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>先の登録の終わりより前に始まる新規を止める（R-I4）。</summary>
    [Fact]
    public async Task 先の登録の終わりより前に始まる新規を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01", "2026-03-31");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: "T9999999999999", partnerId: partner,
                    validFrom: new DateOnly(2026, 3, 30)))],
                save.SaveAsync));

        Assert.Contains("期間が重なっています", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("2023/10/01", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("2026/03/31", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("2026/03/30", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>保存済みの終わりのない登録のあとに足す新規を止め、前の登録を閉じるか、入力した登録年月日を確かめるかを言う</b>（R-I5。再登録の形。REG-23）。
    /// </summary>
    [Fact]
    public async Task 終わりのない登録のあとの新規を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: "T9999999999999", partnerId: partner,
                    validFrom: new DateOnly(2024, 1, 1)))],
                save.SaveAsync));

        // **全文で固める。** 部分一致では「どの行を名指すか」「どちらの直し方を選ぶか」が表明されない
        // ——名指す行がいま入力している行に替わっても緑のままだった（2026-09-24 の全件の REG-24。qa/03 L-66）。
        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "この取引先には「取消・失効年月日」が空の登録（2023/10/01 から）があり、入力している登録（2024/01/01 から）はそのあとに始まります。"
            + "国税庁の公表サイトで、2023/10/01 からの登録の取消年月日か失効年月日が 2024/01/01 以前なら、"
            + "先に取引先の詳細の「登録番号の履歴」でその行の「編集」を開き、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>既にある登録より古い日付で、終わりのない登録を足すと、いま入力している行を名指す</b>（R-I5。過去の登録の後入れの形）。
    /// </summary>
    /// <remarks>
    /// 名指す行は新しく足す行で、一覧に無い——「一覧の「編集」から開け」と言うと、
    /// 文言どおりの次の一手が取れない（2026-09-24 の全件の REG-24）。
    /// </remarks>
    [Fact]
    public async Task 既にある登録より古い終わりのない新規は入力している行を名指す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2026-09-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: "T9999999999999", partnerId: partner,
                    validFrom: new DateOnly(2025, 1, 1)))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "入力している登録（2025/01/01 から）は「取消・失効年月日」が空ですが、そのあとに 2026/09/01 からの登録があります。"
            + "国税庁の公表サイトで、入力している登録の取消年月日か失効年月日が 2026/09/01 以前なら、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>名指す「あとの登録」は、終わりのない行の直後の行である</b>（R-I5）。
    /// </summary>
    /// <remarks>
    /// 終わりをその日までに入れれば通るのは直後の行だけで、もっと後の行を言うと、その日まで伸ばした終わりが
    /// 直後の行と重なる。<b>あとの行が 1 本しか無い検体では、最後の行・いちばん新しい行を言う書き換えも緑になる</b>
    /// ——だから 2 本置く（2026-09-24 の自己レビュー。qa/03 L-46 と同じ縮退の型）。
    /// </remarks>
    [Fact]
    public async Task 名指すあとの登録は直後の行である()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2024-04-01", "2024-12-31");
        InsertRegistration(server, partner, "T9999999999999", "2025-01-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: "T5555555555555", partnerId: partner,
                    validFrom: new DateOnly(2023, 1, 1)))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "入力している登録（2023/01/01 から）は「取消・失効年月日」が空ですが、そのあとに 2024/04/01 からの登録があります。"
            + "国税庁の公表サイトで、入力している登録の取消年月日か失効年月日が 2024/04/01 以前なら、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>同じ保存で足す 2 行のうち、早い行に終わりが無ければ、早い行を名指す</b>（R-I5）。
    /// </summary>
    /// <remarks>
    /// <para><b>画面は 1 行ずつ保存する</b>（docs/14 §5）ので、2 行が一度に来るのは API と取込（フェーズ 6）だけである。
    /// そこには「入力している行」が無い——<b>取込を作る回に、画面に依らない言い方（日付で指す）へ見直す</b>。</para>
    /// <para>それでも残すのは、<b>両方が触った行のとき、早い側の由来で文を選ぶことを固めるのはこの検体だけ</b>だからである
    /// （1 行の保存では、早い側が触った行なら遅い側は必ず保存済みで、書き換えを区別できない）。</para>
    /// </remarks>
    [Fact]
    public async Task 同じ保存で足す二行の早い行に終わりが無ければ早い行を名指す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(
                    Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1)),
                    Registration(no: "T9999999999999", partnerId: partner, validFrom: new DateOnly(2024, 1, 1)))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "入力している登録（2023/10/01 から）は「取消・失効年月日」が空ですが、そのあとに 2024/01/01 からの登録があります。"
            + "国税庁の公表サイトで、入力している登録の取消年月日か失効年月日が 2024/01/01 以前なら、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>既にある登録より古い日付でも、取消・失効年月日があとの登録の始まりの日以前の登録は足せる</b>（R-I5 の断りの条件の境界）。
    /// </summary>
    /// <remarks>
    /// 断りが言う「… 以前なら」の境界そのもの——<b>公表サイトの取消・失効年月日が、ちょうどあとの登録の始まりの日だった場合</b>。
    /// <b>隣接は通す</b>（関門の R-I4 の注記）。1 日でも越えると、次の検体のとおり R-I4 で断る。
    /// <b>あとの登録の始まりの日を入れよ、という意味ではない</b>——断りは公表サイトの日付をそのまま入れよと言う。
    /// </remarks>
    [Fact]
    public async Task 取消失効年月日があとの登録の始まりの日と同じなら足せる()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2026-09-01");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(no: "T9999999999999", partnerId: partner,
                validFrom: new DateOnly(2025, 1, 1), endedOn: new DateOnly(2026, 9, 1), endReason: "revoked"))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>あとの登録の始まりの日を 1 日越えて終わる登録は、R-I4 で断る</b>（上の検体の境界の対）。
    /// </summary>
    [Fact]
    public async Task あとの登録の始まりの日を越えて終わる古い登録は重なりで止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2026-09-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: "T9999999999999", partnerId: partner,
                    validFrom: new DateOnly(2025, 1, 1), endedOn: new DateOnly(2026, 9, 2), endReason: "revoked"))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。2025/01/01 からの登録の「取消・失効年月日」（2026/09/02）が、"
            + "次の登録の「登録年月日」（2026/09/01）より後になっています。「登録年月日」と「取消・失効年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>同じ保存の 2 行どうしでも、期間の重なりを止める。</summary>
    [Fact]
    public async Task 同じ保存の二行の期間の重なりを止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(
                    Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1),
                        endedOn: new DateOnly(2024, 12, 31), endReason: "revoked"),
                    Registration(no: "T9999999999999", partnerId: partner, validFrom: new DateOnly(2024, 1, 1)))],
                save.SaveAsync));

        Assert.Contains("期間が重なっています", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>終わりの記録を消して、後続の登録と重ねる更新を止め、元に戻すよう言う</b>（R-I5）。
    /// </summary>
    /// <remarks>
    /// <b>消したこと自体が重なりを作った</b>ので、「登録年月日を確かめて」は触っていない欄を指す——
    /// 元に戻すのが先で、あとの登録のほうが誤りならそちらを直す（2026-09-24 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 終わりを消して後続と重ねる更新を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var first = InsertRegistration(server, partner, ValidNo, "2023-10-01", "2024-03-31");
        InsertRegistration(server, partner, "T9999999999999", "2024-04-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(id: first, clearEndedOn: true, clearEndReason: true))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "「取消・失効年月日」を空にすると、2024/04/01 からの登録と期間が重なります。"
            + "「取消・失効年月日」と「取消・失効の理由」を元に戻してください。"
            + "2024/04/01 からの登録のほうが誤りなら、先に取引先の詳細の「登録番号の履歴」でその行の「編集」から直してください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>有効な登録の登録年月日を、終わった登録より前に動かす更新を止め、登録年月日の誤りを選べるよう言う</b>（R-I5）。
    /// </summary>
    /// <remarks>
    /// <b>この形で「取消・失効年月日を入れて」だけを言うと、いま使っている登録に誤った終わりを付けて通してしまう</b>
    /// ——隣接なので R-I4 も通り、取引先に有効な登録が 1 件も無い履歴になる（2026-09-24 の自己レビュー）。
    /// 文は「入力している登録の取消年月日か失効年月日があとの登録の始まり以前なら…、そうでなければ登録年月日」と、選ぶ目安を言う。
    /// </remarks>
    [Fact]
    public async Task 有効な登録の登録年月日を前へ動かす更新は目安つきで止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01", "2024-03-31");
        var current = InsertRegistration(server, partner, "T9999999999999", "2024-04-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(id: current, validFrom: new DateOnly(2023, 4, 1)))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "入力している登録（2023/04/01 から）は「取消・失効年月日」が空ですが、そのあとに 2023/10/01 からの登録があります。"
            + "国税庁の公表サイトで、入力している登録の取消年月日か失効年月日が 2023/10/01 以前なら、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>終わった登録の日付を、有効な登録より後ろへ動かす更新を止め、入力した登録年月日を確かめる道も言う</b>（R-I5）。
    /// </summary>
    /// <remarks>
    /// <b>名指すのは保存済みの有効な登録</b>だが、動かしたのはこちらの行である。
    /// 「前の登録を閉じよ」だけを言うと、いま有効な登録を閉じてから直し直す道に誘う（2026-09-24 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 終わった登録を有効な登録より後ろへ動かす更新は登録年月日の道も言う()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2024-04-01");
        var ended = InsertRegistration(server, partner, "T9999999999999", "2022-01-01", "2022-12-31");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(id: ended,
                    validFrom: new DateOnly(2025, 1, 1), endedOn: new DateOnly(2025, 12, 31)))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "この取引先には「取消・失効年月日」が空の登録（2024/04/01 から）があり、入力している登録（2025/01/01 から）はそのあとに始まります。"
            + "国税庁の公表サイトで、2024/04/01 からの登録の取消年月日か失効年月日が 2025/01/01 以前なら、"
            + "先に取引先の詳細の「登録番号の履歴」でその行の「編集」を開き、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>終わりの日を伸ばして、後続の登録に食い込む更新を止める（R-I4）。</summary>
    [Fact]
    public async Task 終わりを伸ばして後続に食い込む更新を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var first = InsertRegistration(server, partner, ValidNo, "2023-10-01", "2024-03-31");
        InsertRegistration(server, partner, "T9999999999999", "2024-04-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(id: first, endedOn: new DateOnly(2024, 6, 30)))],
                save.SaveAsync));

        Assert.Contains("期間が重なっています", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>保存済みの行どうしの違反は、触らない保存を止めない。</b>
    /// トリガ導入（2026-09-02）前に入った古いデータがあっても、無関係な行は直せる。
    /// </summary>
    /// <remarks>DDL のトリガが今は同じ形を拒むので、古いデータはトリガを外して再現する。</remarks>
    [Fact]
    public async Task 保存済みどうしの違反は触らない行の保存を止めない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var early = InsertRegistration(server, partner, "T5555555555555", "2020-01-01", "2020-12-31");
        server.Execute("drop trigger if exists trg_partner_invoice_registrations_no_overlap_insert");
        server.Execute("drop trigger if exists trg_partner_invoice_registrations_no_overlap_update");
        InsertRegistration(server, partner, ValidNo, "2023-10-01");
        InsertRegistration(server, partner, "T9999999999999", "2024-01-01");
        var save = new SaveSpy();

        // 違反ペア（2023-10-01 と 2024-01-01。どちらも終わりが無い）は触らず、その前の行だけ直す。
        await Gate(server).SubmitAsync(
            [Updating(Registration(id: early, endedOn: new DateOnly(2020, 6, 30)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>別の取引先の期間とは重ねて数えない（軸は取引先ごと）。</summary>
    [Fact]
    public async Task 別の取引先の期間とは重ねて数えない()
    {
        using var server = new PartnerServer();
        var owner = InsertPartner(server, "P901");
        var other = InsertPartner(server, "P902");
        InsertRegistration(server, owner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(no: "T9999999999999", partnerId: other, validFrom: new DateOnly(2024, 1, 1)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>古い違反データが間に挟まっても、触った行が絡む違反は見逃さない。</b>
    /// 隣どうしだけ比べると、違反ペアを 1 つ挟んだ先の行が素通りする（2026-09-02 のレビュー指摘）。
    /// </summary>
    [Fact]
    public async Task 古い違反データを挟んでも触った行の違反を見逃さない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        server.Execute("drop trigger if exists trg_partner_invoice_registrations_no_overlap_insert");
        server.Execute("drop trigger if exists trg_partner_invoice_registrations_no_overlap_update");
        InsertRegistration(server, partner, ValidNo, "2023-10-01");                       // 終わりが無い
        InsertRegistration(server, partner, "T9999999999999", "2024-02-01", "2024-03-01"); // ↑の中（違反）
        var save = new SaveSpy();

        // 新しい行は、閉じた 2024-03-01 の隣ではなく、開いた 2023-10-01 の中にある
        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: "T5555555555555", partnerId: partner,
                    validFrom: new DateOnly(2024, 4, 1)))],
                save.SaveAsync));

        Assert.Equal(
            "登録できません。登録の期間が重なっています。"
            + "この取引先には「取消・失効年月日」が空の登録（2023/10/01 から）があり、入力している登録（2024/04/01 から）はそのあとに始まります。"
            + "国税庁の公表サイトで、2023/10/01 からの登録の取消年月日か失効年月日が 2024/04/01 以前なら、"
            + "先に取引先の詳細の「登録番号の履歴」でその行の「編集」を開き、"
            + "その取消年月日か失効年月日をそのまま「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。"
            + "そうでなければ、入力している登録の「登録年月日」を確かめてください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>閉じた新規の行が、既存の行の前に始まって食い込む形も止める（逆向きの R-I4）。</summary>
    [Fact]
    public async Task 既存の行の前に始まって食い込む新規を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: "T9999999999999", partnerId: partner,
                    validFrom: new DateOnly(2020, 1, 1),
                    endedOn: new DateOnly(2023, 10, 2), endReason: "expired"))],
                save.SaveAsync));

        Assert.Contains("期間が重なっています", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>逆向きの隣接（新規の行の終わり＝既存の行の始まり）は通す。</summary>
    [Fact]
    public async Task 既存の行の始まりの日に終わる新規は通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Registration(no: "T9999999999999", partnerId: partner,
                validFrom: new DateOnly(2020, 1, 1),
                endedOn: new DateOnly(2023, 10, 1), endReason: "expired"))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>更新で登録年月日を、前の行の期間の中へ動かす形も止める。</summary>
    [Fact]
    public async Task 登録年月日を前の行の期間の中へ動かす更新を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01", "2024-03-31");
        var moving = InsertRegistration(server, partner, "T9999999999999", "2024-04-01", "2024-12-31");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Updating(Registration(id: moving, validFrom: new DateOnly(2024, 2, 1)))],
                save.SaveAsync));

        Assert.Contains("期間が重なっています", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    // --- 取引先の実在（docs/14 §5 R-I9）と行の削除（R-I8）---

    /// <summary>実在しない取引先への新規の行を、言葉で断る（外部キーの生エラーにしない）。</summary>
    [Fact]
    public async Task 実在しない取引先への新規を止める()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [Adding(Registration(no: ValidNo, partnerId: 999999,
                    validFrom: new DateOnly(2023, 10, 1)))],
                save.SaveAsync));

        Assert.Contains("取引先が見つかりません", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("入り直してください", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>更新の行が混ざっていても、新規の行があるなら実在の検査は掛かる。</summary>
    [Fact]
    public async Task 実在しない取引先は更新の行が混ざっても止める()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                    Add = [Registration(no: ValidNo, partnerId: 999999, validFrom: new DateOnly(2023, 10, 1))],
                    Update = [Registration(partnerId: 999999, id: 900)],
                }],
                save.SaveAsync));
        Assert.False(save.Called);
    }

    /// <summary>更新の行だけなら実在は見ない（保存済みに紐づかない更新は黙って通す倒し方と同じ）。</summary>
    [Fact]
    public async Task 実在しない取引先でも更新の行だけなら実在を見ない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Registration(partnerId: 999999, id: 900))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>仮の識別子（同じ保存で作る取引先）は実在の検査に掛けない。</summary>
    [Fact]
    public async Task 仮の識別子の取引先は実在の検査に掛けない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var row = Registration(no: ValidNo, validFrom: new DateOnly(2023, 10, 1));
        row.Fields["Partner"] = new IdFieldData { Value = "@temporary:aaaa" };

        await Gate(server).SubmitAsync([Adding(row)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>登録の行の削除を止める（R-I8。CanDelete: false は画面の形であって守りではない）。</summary>
    [Fact]
    public async Task 登録の行の削除を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var row = InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                    Delete = [new ModuleDeleteInfo
                    {
                        ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                        Id = row.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }],
                }],
                save.SaveAsync));

        // **次の一手を画面の字で言う**——「一覧」は画面の見出しに無い字だった（2026-09-24 の自己レビュー）。
        // **見出しは操作で決まる**——削除を「登録できません」と断っていた（同日。見出しの網を広げて見つけた）。
        // **「行は消さない」とは言わない**——帳簿に写っていない行は消せると決まっている（ADR-0063。実装はフェーズ 6）。
        // **入力の誤りに取消・失効を記録させない**——一手を 2 つに分けて言う。
        Assert.Equal(
            "削除できません。入力を誤った行なら、取引先の詳細の「登録番号の履歴」でその行の「編集」を開き、正しい値に直してください。"
            + "登録が取り消されたか失効したのなら、同じ「編集」で、国税庁の公表サイトの取消年月日か失効年月日をそのまま"
            + "「取消・失効年月日」に入れて「取消・失効の理由」を選んでください。",
            thrown.Message);
        Assert.False(save.Called);
    }

    /// <summary>別のモジュールの削除は素通しする。</summary>
    [Fact]
    public async Task 別のモジュールの削除は素通しする()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [new ModuleSubmitData
            {
                ModuleName = "Partner",
                Delete = [new ModuleDeleteInfo { ModuleName = "Partner", Id = "1" }],
            }],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>日付が動かない行は、除外しない。</b>
    /// </summary>
    /// <remarks>
    /// 「同じ保存に載っている行は全部除外する」と実装すると、公表名だけを直した行の日付へ
    /// 新しい行を足せてしまう（<b>二重登録が通る</b>）。除外してよいのは
    /// <b>登録年月日が差分にある行</b>だけである。
    /// </remarks>
    [Fact]
    public async Task 公表名だけを直した行の日付には新しい登録を足せない()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var existing = InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var untouched = Registration(id: existing);
        untouched.Fields["PublishedName"] = new TextFieldData { Value = "公表名を直しただけ" };

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                    Add = [Registration(no: "T9999999999999", partnerId: partner, validFrom: new DateOnly(2023, 10, 1))],
                    Update = [untouched],
                }],
                save.SaveAsync));
        Assert.False(save.Called);
    }

    /// <summary>想定していない型で取引先が来たら、素通しではなく止める。</summary>
    /// <remarks>
    /// <b><c>null</c> に落とすと保存済みの取引先へ落ちる</b>ので、登録期間の重なりを
    /// <b>別の取引先の行と突き合わせる</b>ことになる——<b>止めるべき二重登録を通し、
    /// 通すべき登録を止める</b>両方が起きうる。「CLB は宣言した型でしか送らない」は
    /// 素通しの理由にならない（型を変えるのはデザインを触る人である。2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 想定していない型の取引先なら止める()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var first = Registration(no: ValidNo, validFrom: new DateOnly(2023, 10, 1));
        first.Fields["Partner"] = new NumberFieldData { Value = 3m };

        var thrown = await Assert.ThrowsAsync<UnreadableFieldException>(
            () => Gate(server).SubmitAsync([Adding(first)], save.SaveAsync));

        Assert.Equal("Partner", thrown.Field);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>登録番号が別の型で届いたら止める</b>——書式の検査（R-I7）が黙って素通しにならない。
    /// </summary>
    /// <remarks>
    /// DB は登録番号の書式を見ない（docs/14 §7）ので、ここが素通しになると壊れた番号が保存され、計上時の写しに焼き込まれる
    /// （2026-09-24 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 想定していない型の登録番号なら止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        var row = Registration(partnerId: partner, validFrom: new DateOnly(2025, 1, 1));
        row.Fields["RegistrationNo"] = new SelectFieldData { Value = "T123" };

        var thrown = await Assert.ThrowsAsync<UnreadableFieldException>(
            () => Gate(server).SubmitAsync([Adding(row)], save.SaveAsync));

        Assert.Equal("RegistrationNo", thrown.Field);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>理由だけ残して終わりを消すと、対（R-I1）と重なり（R-I5）を 1 通で言う</b>——対は期間を崩さない。
    /// </summary>
    /// <remarks>
    /// 画面で「取消・失効年月日」だけを消すのは自然な操作である。対の断りだけを先に返すと、従って両方を消した 2 回目で
    /// 初めて「空にすると重なる。元に戻して」が出る——従っても通らない一手を先に並べたことになる（2026-09-24 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 理由だけ残して終わりを消すと対と重なりを一度で言う()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var first = InsertRegistration(server, partner, ValidNo, "2023-10-01", "2024-04-01");
        InsertRegistration(server, partner, "T9999999999999", "2024-04-01");

        var message = await RejectedMessage(server, Updating(Registration(id: first, clearEndedOn: true)));

        Assert.Equal(
            "登録できません（2 件）。"
            + "①「取消・失効年月日」と「取消・失効の理由」は、両方入力するか、両方空にしてください。"
            + "②登録の期間が重なっています。「取消・失効年月日」を空にすると、2024/04/01 からの登録と期間が重なります。"
            + "「取消・失効年月日」と「取消・失効の理由」を元に戻してください。"
            + "2024/04/01 からの登録のほうが誤りなら、先に取引先の詳細の「登録番号の履歴」でその行の「編集」から直してください。",
            message);
    }

    /// <summary>
    /// <b>付け替えを断った行でも、行の中だけで決まる規則は言う</b>——断りに従って取引先を戻せば、残りの変更はもとの取引先に当たる。
    /// </summary>
    [Fact]
    public async Task 付け替えを断った行でも行の中の規則は言う()
    {
        using var server = new PartnerServer();
        var a = InsertPartner(server, "P881");
        var b = InsertPartner(server, "P882");
        var row = InsertRegistration(server, a, ValidNo, "2024-01-01");

        var message = await RejectedMessage(server, Updating(Registration(
            partnerId: b, endedOn: new DateOnly(2024, 6, 30), id: row)));

        Assert.Equal(
            "登録できません（2 件）。"
            + "①登録の「取引先」は、保存したあとは変更できません。別の取引先の登録にするときは、その取引先の画面で入力し直してください。"
            + "②「取消・失効年月日」と「取消・失効の理由」は、両方入力するか、両方空にしてください。",
            message);
    }

    /// <summary>
    /// <b>付け替えを断った行は、送られてきた取引先の側で同じ日の 2 件に数えない</b>——同じ保存で足す行と同じ日でも。
    /// </summary>
    /// <remarks>
    /// 同じ保存の中の 2 件（<c>DuplicatesWithinAsync</c>）に、付け替えを断った行を入れると、移れない先の取引先の話になる。
    /// </remarks>
    [Fact]
    public async Task 付け替えを断った行は同じ保存の新しい行とも同じ日の二件に数えない()
    {
        using var server = new PartnerServer();
        var a = InsertPartner(server, "P883");
        var b = InsertPartner(server, "P884");
        var row = InsertRegistration(server, a, ValidNo, "2025-01-01");

        var message = await RejectedMessage(
            server,
            new ModuleSubmitData
            {
                ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                Update = [Registration(partnerId: b, validFrom: new DateOnly(2025, 1, 1), id: row)],
                Add = [Registration("T9999999999999", b, new DateOnly(2025, 1, 1))],
            });

        Assert.Equal(
            "登録できません。登録の「取引先」は、保存したあとは変更できません。別の取引先の登録にするときは、その取引先の画面で入力し直してください。",
            message);
    }

    /// <summary>
    /// <b>取引先が見つからなければ、その取引先の重なりは言わない</b>——履歴を組み立てる相手がいない。
    /// </summary>
    [Fact]
    public async Task 取引先が見つからなければ重なりは言わない()
    {
        using var server = new PartnerServer();

        var message = await RejectedMessage(server, Adding(
            Registration(ValidNo, 9999, new DateOnly(2025, 1, 1)),
            Registration("T9999999999999", 9999, new DateOnly(2025, 6, 1))));

        Assert.Equal(
            "登録できません。取引先が見つかりません。取引先の詳細の「登録番号を追加する」から入り直してください。",
            message);
    }

    /// <summary>取引先の欄が空文字で来たら、突き合わせの対象にしない。</summary>
    [Fact]
    public async Task 空の取引先は突き合わせに使わない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var first = Registration(no: ValidNo, validFrom: new DateOnly(2023, 10, 1));
        var second = Registration(no: "T9999999999999", validFrom: new DateOnly(2023, 10, 1));
        first.Fields["Partner"] = new IdFieldData { Value = string.Empty };
        second.Fields["Partner"] = new IdFieldData { Value = string.Empty };

        await Gate(server).SubmitAsync([Adding(first, second)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>相手を決められない行は数えない。</b>
    /// </summary>
    /// <remarks>
    /// 取引先が差分に無く、行の識別子からも引けない（保存済みでない）とき、
    /// この検査は相手を知りようがない。<b>知らないまま「同じ相手だ」と数えると、
    /// 無関係な行どうしで止まる。</b> その行が本当に壊れているなら、別の関門か DB が止める。
    /// </remarks>
    [Fact]
    public async Task 相手を決められない行は二重登録に数えない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(
                Registration(validFrom: new DateOnly(2026, 1, 1), id: 900),
                Registration(validFrom: new DateOnly(2026, 1, 1), id: 901))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// 引数を渡さなければ止まる。<b>引数名まで表明する。</b>
    /// </summary>
    /// <remarks>
    /// 型だけを見ると、ガードを消しても中の LINQ が同じ
    /// <see cref="ArgumentNullException"/>（<c>ParamName</c> は <c>"source"</c>）を投げるので
    /// <b>テストは通ったまま</b>になる（2026-08-27 の自己レビュー R16-04）。
    /// </remarks>
    [Fact]
    public async Task 引数を渡さなければ止まる()
    {
        using var server = new PartnerServer();
        var gate = Gate(server);

        var noData = await Assert.ThrowsAsync<ArgumentNullException>(
            () => gate.SubmitAsync(null!, () => Task.FromResult(new List<ModuleSubmitResult>())));
        Assert.Equal("transactionData", noData.ParamName);

        var noSave = await Assert.ThrowsAsync<ArgumentNullException>(() => gate.SubmitAsync([], null!));
        Assert.Equal("save", noSave.ParamName);
    }
}
