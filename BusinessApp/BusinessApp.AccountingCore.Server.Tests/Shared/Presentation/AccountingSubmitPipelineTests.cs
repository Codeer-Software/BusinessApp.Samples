namespace BusinessApp.AccountingCore.Server.Tests.Shared.Presentation;

using BusinessApp.Partners.Server;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Journals.Application;
using BusinessApp.AccountingCore.Server.Masters.Application;
using BusinessApp.AccountingCore.Server.Settings.Application;
using BusinessApp.AccountingCore.Server.Shared.Presentation;

/// <summary>
/// 保存の関門のつなぎ方（<see cref="AccountingSubmitPipeline"/>）。
/// </summary>
/// <remarks>
/// <para><b>つなぎ方そのものを検査する。</b> 本番の配線は <c>BusinessApp.Server</c> にあり、
/// そこは<b>カバレッジにもミューテーションにも載らない</b>（ADR-0012 §3）。
/// 関門を足したのに配線し忘れても、他のテストは全部緑のままになる
/// （2026-08-26 の自己レビュー指摘。qa/02）。</para>
/// <para>ここが落ちるのは「関門が 1 つ外れた」ときだけである。</para>
/// </remarks>
public class AccountingSubmitPipelineTests
{
    private static ModuleData Registration(string no, long partnerId, DateOnly validFrom)
    {
        var data = new ModuleData { Name = PartnerRegistrationSubmitGate.ModuleName };
        data.Fields["RegistrationNo"] = new TextFieldData { Value = no };
        data.Fields["Partner"] = new LinkFieldData
        {
            Value = partnerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        data.Fields["ValidFrom"] = new DateFieldData { Value = validFrom };
        return data;
    }

    private static long InsertPartner(AccountingServer server)
    {
        server.Execute("insert into partners (code, name, is_active) values ('P900', '株式会社ベガ商会', 1)");
        return server.Scalar<long>("select last_insert_rowid()");
    }

    /// <summary>
    /// <b>登録の関門がつながっている。</b> つながっていなければ壊れた番号が保存される。
    /// </summary>
    [Fact]
    public async Task 壊れた登録番号は保存に届かない()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server);
        var called = false;

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                    Add = [Registration("12345", partner, new DateOnly(2023, 10, 1))],
                }],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(called);
    }

    /// <summary>
    /// <b>仕訳の関門もつながっている。</b> 計上済みで送られた伝票は、いったん下書きに戻される
    /// （<c>JournalSubmitGate</c> の役目）。
    /// </summary>
    [Fact]
    public async Task 計上済みで送られた伝票は下書きに戻される()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.Entry("1", status: "posted");

        await Assert.ThrowsAnyAsync<Exception>(
            () => server.Pipeline.SubmitAsync(
                [SubmitData.Updating(entry)],
                () => Task.FromResult(new List<ModuleSubmitResult>())));

        Assert.Equal("draft", SubmitData.SelectValue(entry, "Status"));
    }

    /// <summary>
    /// <b>マスタの関門もつながっている</b>（ADR-0038）。つながっていなければ、使用中の科目の科目区分が
    /// 「更新しました」で変わる（qa/03 L-29）。
    /// </summary>
    [Fact]
    public async Task 使用中の科目の科目区分は保存に届かない()
    {
        using var server = new AccountingServer();
        // 未払金 1,000 ／ 現金 1,000（未払金を現金で払う）。現金が「使用中」になる
        server.InsertPosted(1, "支払", "2026-08-24", ("debit", "2200", 1000), ("credit", "1100", 1000));
        var cash = new ModuleData { Name = "Account" };
        cash.Fields["Id"] = new IdFieldData { Value = server.Text(server.AccountOf("1100").Value) };
        cash.Fields["Category"] = new SelectFieldData { Value = "expense" };
        var called = false;

        await Assert.ThrowsAsync<BusinessApp.AccountingCore.Server.Masters.Application.MasterRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [new ModuleSubmitData { ModuleName = "Account", Update = [cash] }],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(called);
    }

    /// <summary>
    /// <b>同じ保存に仕訳と登録が混ざっても、どちらの関門も効く。</b>
    /// 片方だけ通す配線になっていたら、ここが落ちる。
    /// </summary>
    [Fact]
    public async Task 仕訳と登録が同じ保存に混ざっても両方の関門が効く()
    {
        using var server = new AccountingServer();
        var partner = InsertPartner(server);
        var entry = SubmitData.Entry("1", status: "posted");
        var called = false;

        await Assert.ThrowsAsync<PartnerRegistrationRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [
                    SubmitData.Updating(entry),
                    new ModuleSubmitData
                    {
                        ModuleName = PartnerRegistrationSubmitGate.ModuleName,
                        Add = [Registration("12345", partner, new DateOnly(2023, 10, 1))],
                    },
                ],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        // 仕訳の関門が先に走って下書きに書き換え、登録の関門が保存を止めた。
        Assert.Equal("draft", SubmitData.SelectValue(entry, "Status"));
        Assert.False(called);
    }

    /// <summary>
    /// <b>取引先の関門もつながっている。</b> つながっていなければ、検査用数字の合わない法人番号が
    /// そのまま保存され、名寄せの自然キーになる（docs/13 §2-2）。
    /// </summary>
    [Fact]
    public async Task 検査用数字の合わない法人番号は保存に届かない()
    {
        using var server = new AccountingServer();
        var partner = new ModuleData { Name = PartnerSubmitGate.ModuleName };
        partner.Fields["CorporateNumber"] = new TextFieldData { Value = "1700110005901" };
        var called = false;

        await Assert.ThrowsAsync<PartnerRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [new ModuleSubmitData { ModuleName = PartnerSubmitGate.ModuleName, Add = [partner] }],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(called);
    }

    [Fact]
    public async Task 保存を渡さなければ止まる()
    {
        using var server = new AccountingServer();

        await Assert.ThrowsAsync<ArgumentNullException>(() => server.Pipeline.SubmitAsync([], null!));
    }

    /// <summary>
    /// <b>関門が拾えなかった保存の失敗も、利用者には利用者の語で届く</b>（qa/01 F-16）。
    /// </summary>
    /// <remarks>
    /// CLB は保存の失敗を例外ではなく <c>ExceptionMessage</c> に詰め、その中身を
    /// そのままトーストに出す。差し替えを忘れると
    /// <c>SQLite Error 19: 'NOT NULL constraint failed: …'</c> が利用者の画面に出る
    /// （実機で踏んだ形。qa/03 L-16）。
    /// </remarks>
    [Fact]
    public async Task 保存の失敗は利用者の語に差し替わる()
    {
        using var server = new AccountingServer();

        var results = await server.Pipeline.SubmitAsync(
            [],
            () => Task.FromResult(new List<ModuleSubmitResult>
            {
                SubmitData.Failure("SQLite Error 19: 'NOT NULL constraint failed: journal_lines.account_id'."),
                SubmitData.Result("@temporary:0f0a", "1"),
            }));

        // 成功した結果には触らない（仮 ID の対応表が消えると、計上が ID を解決できなくなる）。
        Assert.Equal(
            [SaveFailureMessage.Text, string.Empty],
            results.Select(r => r.ExceptionMessage ?? string.Empty));
        Assert.Equal(["", "1"], results.Select(r => r.DestinationId ?? string.Empty));
    }

    // --- CLB へ返す形（SubmitAsResultAsync。ADR-0051）------------------------------------------

    /// <summary>
    /// <b>差し戻しは例外ではなく結果で返す。</b> 例外で返すと CLB が「更新に失敗しました」をもう 1 枚出す（qa/02 R25-08）。
    /// 文言は関門のもの（利用者の語）がそのまま載り、保存には渡らない。
    /// </summary>
    [Fact]
    public async Task 差し戻しは利用者の語のまま結果で返り_保存には渡らない()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntryWithout("@temporary:0f0a", "TransactionDate");
        var saved = false;

        var results = await server.Pipeline.SubmitAsResultAsync(
            [SubmitData.Adding(entry)], () => { saved = true; return Task.FromResult(new List<ModuleSubmitResult>()); });

        var message = Assert.Single(results).ExceptionMessage;
        Assert.StartsWith("保存できません。①", message, StringComparison.Ordinal);
        Assert.Contains("取引日", message, StringComparison.Ordinal);
        Assert.False(saved);
        Assert.Empty(server.SaveFailureLog);
    }

    /// <summary>
    /// <b>差し戻し以外の例外は、利用者には定型文だけを見せる</b>（開発者向けの文言を画面に出さない。qa/02 R57-35）。
    /// 原文はログへ。<c>DbTransactionScope</c> を使わない本番の経路では CLB が結果を見て巻き戻す（qa/01 F-42）。
    /// </summary>
    [Fact]
    public async Task 想定外の失敗は定型文で返り_原文はログに残る()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry("@temporary:0f0a", status: "draft");

        var results = await server.Pipeline.SubmitAsResultAsync(
            [SubmitData.Adding(entry)], () => throw new InvalidOperationException("仮 ID @temporary:0f0a に本物の ID が 2 つ対応している"));

        Assert.Equal([SaveFailureMessage.Text], results.Select(r => r.ExceptionMessage));
        var logged = Assert.Single(server.SaveFailureLog);
        Assert.Contains("仮 ID", logged, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", logged, StringComparison.Ordinal);
    }

    /// <summary>通った保存はそのまま返す（CLB の結果に触らない）。引数の欠けは呼び手の誤りなので定型文にしない。</summary>
    [Fact]
    public async Task 通った保存の結果はそのまま返り_引数の欠けは止まる()
    {
        using var server = new AccountingServer();
        var entry = SubmitData.NewEntry("@temporary:0f0a", status: "draft");

        var results = await server.Pipeline.SubmitAsResultAsync(
            [SubmitData.Adding(entry)], () => Task.FromResult(new List<ModuleSubmitResult> { SubmitData.Result("@temporary:0f0a", "1") }));

        Assert.Equal(["1"], results.Select(r => r.DestinationId));
        Assert.All(results, r => Assert.True(string.IsNullOrEmpty(r.ExceptionMessage)));
        await Assert.ThrowsAsync<ArgumentNullException>(() => server.Pipeline.SubmitAsResultAsync([], null!));
    }

    /// <summary>
    /// <b>自社情報の関門がつながっている。</b> つながっていなければ打ち間違えた法人番号が保存される。
    /// </summary>
    /// <remarks>
    /// 会計コアの関門は 2 本になった（仕訳・自社情報）。<b>本番の配線は検査に載らない</b>ので、
    /// 足した関門をここに繋いだかどうかは、このテストだけが見ている。
    /// </remarks>
    [Fact]
    public async Task 検査用数字の合わない自社の法人番号は保存に届かない()
    {
        using var server = new AccountingServer();
        var profile = new ModuleData { Name = CompanyProfileSubmitGate.ModuleName };
        profile.Fields["CorporateNumber"] = new TextFieldData { Value = "1835678256246" };
        var called = false;

        await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => server.Pipeline.SubmitAsync(
                [new ModuleSubmitData
                {
                    ModuleName = CompanyProfileSubmitGate.ModuleName,
                    Update = [profile],
                }],
                () =>
                {
                    called = true;
                    return Task.FromResult(new List<ModuleSubmitResult>());
                }));

        Assert.False(called);
    }
}
