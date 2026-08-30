namespace BusinessApp.Partners.Server.Tests;

using BusinessApp.Partners.Server;
using BusinessApp.Partners.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 登録を保存するときの関門（docs/07 §3-2）。
/// </summary>
/// <remarks>
/// <b>止めるのは書式と、同じ日から始まる二重登録の 2 つだけ</b>。
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
    private static ModuleData Registration(
        string? no = null, long? partnerId = null, DateOnly? validFrom = null, long? id = null)
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
            // 参照フィールド（LinkFieldDesign）のデータは LinkFieldData で、識別子は Value に入る。
            // ModuleFieldData.Id と取り違えると、関門はいつも null を見て素通しする。
            data.Fields["Partner"] = new LinkFieldData
            {
                Value = partner.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }

        if (validFrom is DateOnly from)
        {
            data.Fields["ValidFrom"] = new DateFieldData { Value = from };
        }

        return data;
    }

    private static long InsertPartner(PartnerServer server, string code = "P900")
    {
        server.Execute($"insert into partners (code, name, is_active) values ('{code}', '取引先 {code}', 1)");
        return server.Scalar<long>("select last_insert_rowid()");
    }

    private static long InsertRegistration(
        PartnerServer server, long partnerId, string no, string validFrom)
    {
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partnerId}, '{no}', {PartnerServer.DateLiteral(validFrom)})
            """);
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

        Assert.Contains("登録番号の形が違います", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("14 桁", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);   // トーストは改行できない（qa/01 D-12）
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

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync([Updating(Registration("T12"))], save.SaveAsync));

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

        Assert.Contains("2023/10/01", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", thrown.Message, StringComparison.Ordinal);
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
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, 'T1111111111111', {PartnerServer.DateLiteral("2023-10-01")})
            """);
        server.Execute($"""
            insert into partner_invoice_registrations (partner_id, registration_no, valid_from)
            values ({partner}, '{ValidNo}', {PartnerServer.DateLiteral("2026-04-01")})
            """);
        var moving = server.Scalar<long>("select last_insert_rowid()");
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

    // --- 同じ保存の中の二重登録（登録の入力を取引先の詳細に置いたので実際に起こる。docs/07 §3-4）---

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

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => Gate(server).SubmitAsync([submit], save.SaveAsync));
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
        first.Fields["Partner"] = new LinkFieldData { Value = "@temporary:aaaa-bbbb" };
        second.Fields["Partner"] = new LinkFieldData { Value = "@temporary:aaaa-bbbb" };

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
        first.Fields["Partner"] = new LinkFieldData { Value = "@temporary:aaaa" };
        second.Fields["Partner"] = new LinkFieldData { Value = "@temporary:bbbb" };

        await Gate(server).SubmitAsync([Adding(first, second)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>同じ相手でも、始まる日が違えば通す（登録 → 取消 → 再登録の履歴）。</summary>
    [Fact]
    public async Task 同じ相手でも始まる日が違えば通す()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(
                Registration(no: ValidNo, partnerId: partner, validFrom: new DateOnly(2023, 10, 1)),
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
        var first = InsertRegistration(server, partner, ValidNo, "2023-10-01");
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

    /// <summary>
    /// <b>親 FK が識別子フィールドで来ても、二重登録を止める。</b>
    /// </summary>
    /// <remarks>
    /// 登録の入力を取引先の詳細に移すと、親 FK は <c>IdFieldDesign</c> になる
    /// （ヘッダ＋明細の正典。qa/01 D-17）。<b>参照フィールド決め打ちだと、その日に
    /// この関門が丸ごと素通しに落ちる</b>——しかもフィクスチャが自分で
    /// <c>LinkFieldData</c> を組むので、既存のテストは全部緑のままである
    /// （2026-08-31 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 親FKが識別子フィールドでも二重登録を止める()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var save = new SaveSpy();

        var row = Registration(no: "T9999999999999", validFrom: new DateOnly(2023, 10, 1));
        row.Fields["Partner"] = new IdFieldData
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
        added.Fields["Partner"] = new LinkFieldData
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
    [Fact]
    public async Task 同じ保存で二行の登録年月日を入れ替えられる()
    {
        using var server = new PartnerServer();
        var partner = InsertPartner(server);
        var a = InsertRegistration(server, partner, ValidNo, "2023-10-01");
        var b = InsertRegistration(server, partner, "T9999999999999", "2025-04-01");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(
                Registration(validFrom: new DateOnly(2025, 4, 1), id: a),
                Registration(validFrom: new DateOnly(2023, 10, 1), id: b))],
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

    /// <summary>想定していない型で取引先が来たら、突き合わせの対象にしない。</summary>
    /// <remarks>
    /// CLB は宣言した型でしか送らないので、ここに来るのは API を直に叩いた経路だけである。
    /// 例外にせず素通しするのは、<b>止めるのは書式と二重登録の 2 つだけ</b>という
    /// この関門の約束（docs/07 §3-2）を広げないため。DB の NOT NULL が最後に受け止める。
    /// </remarks>
    [Fact]
    public async Task 想定していない型の取引先は突き合わせに使わない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var first = Registration(no: ValidNo, validFrom: new DateOnly(2023, 10, 1));
        var second = Registration(no: "T9999999999999", validFrom: new DateOnly(2023, 10, 1));
        first.Fields["Partner"] = new NumberFieldData { Value = 3m };
        second.Fields["Partner"] = new NumberFieldData { Value = 3m };

        await Gate(server).SubmitAsync([Adding(first, second)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>取引先の欄が空文字で来たら、突き合わせの対象にしない。</summary>
    [Fact]
    public async Task 空の取引先は突き合わせに使わない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        var first = Registration(no: ValidNo, validFrom: new DateOnly(2023, 10, 1));
        var second = Registration(no: "T9999999999999", validFrom: new DateOnly(2023, 10, 1));
        first.Fields["Partner"] = new LinkFieldData { Value = string.Empty };
        second.Fields["Partner"] = new LinkFieldData { Value = string.Empty };

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
