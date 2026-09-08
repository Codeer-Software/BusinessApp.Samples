namespace BusinessApp.AccountingCore.Server.Tests.Masters;

using System.Globalization;

using BusinessApp.AccountingCore.Server.Masters;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// マスタの値の関門（docs/04 §1 の B-1・B-2）。
/// </summary>
/// <remarks>
/// <para><b>DDL に当たると全部が同じ定型文になっていた</b>（qa/03 L-28）。
/// ここが見るのは「利用者の語で断れているか」であって、DB が止めるかどうかではない
/// （そちらは <c>MasterCodeGuardTests</c> と <c>SchemaConstraintTests</c>）。</para>
/// <para><b>差分は実機の形で組む</b>（qa/01 F-11・F-12）——更新は識別子と触った欄だけが届く。</para>
/// </remarks>
public class MasterSubmitGateTests
{
    private static ModuleSubmitData Adding(string module, ModuleData data)
        => new() { ModuleName = module, Add = [data] };

    private static ModuleSubmitData Updating(string module, ModuleData data)
        => new() { ModuleName = module, Update = [data] };

    private static ModuleData New(string module, params (string Field, FieldDataBase Value)[] fields)
    {
        var data = new ModuleData { Name = module };
        data.Fields["Id"] = new IdFieldData { Value = "@temporary:1" };
        foreach (var (field, value) in fields)
        {
            data.Fields[field] = value;
        }

        return data;
    }

    private static ModuleData Row(string module, long id, params (string Field, FieldDataBase Value)[] fields)
    {
        var data = new ModuleData { Name = module };
        data.Fields["Id"] = new IdFieldData { Value = id.ToString(CultureInfo.InvariantCulture) };
        foreach (var (field, value) in fields)
        {
            data.Fields[field] = value;
        }

        return data;
    }

    private static (string Field, FieldDataBase Value) Text(string field, string? value)
        => (field, new TextFieldData { Value = value });

    private static (string Field, FieldDataBase Value) Select(string field, string? value)
        => (field, new SelectFieldData { Value = value });

    private static async Task<bool> Submit(AccountingServer server, params ModuleSubmitData[] data)
    {
        var called = false;
        await server.Pipeline.SubmitAsync(data, () =>
        {
            called = true;
            return Task.FromResult(new List<ModuleSubmitResult>());
        });
        return called;
    }

    private static async Task<MasterRejectedException> Rejected(AccountingServer server, params ModuleSubmitData[] data)
    {
        var called = false;
        var thrown = await Assert.ThrowsAsync<MasterRejectedException>(
            () => server.Pipeline.SubmitAsync(data, () =>
            {
                called = true;
                return Task.FromResult(new List<ModuleSubmitResult>());
            }));
        Assert.False(called);
        return thrown;
    }

    // --- コードの重複（B-1 の 1 例目） -------------------------------------------

    /// <summary>
    /// 既にあるコードは、利用者の語で断る。
    /// </summary>
    /// <remarks>
    /// <b>これが定型文になっていた</b>（qa/03 L-28 の 1 例目。「保存できませんでした。入力内容を確かめ…」）。
    /// </remarks>
    [Fact]
    public async Task 既にあるコードは断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(
            server,
            Adding("Account", New("Account", Text("Code", "1100"), Text("Name", "現金その 2"))));

        Assert.Contains("科目コード", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("1100", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("既に使われています", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>大小だけが違うコードも断り、ぶつかった相手の字を見せる</b>（ADR-0047 の決定 7）。
    /// </summary>
    /// <remarks>
    /// 字を見せないと、利用者は「1100 は入っていないのに」と思う。
    /// </remarks>
    [Fact]
    public async Task 大小だけが違うコードは相手の字を見せて断る()
    {
        using var server = new AccountingServer();

        // 初期データの税区分 OUT（対象外）に、小文字で当てる。
        var thrown = await Rejected(server, Adding("TaxCategory", New(
            "TaxCategory",
            Text("Code", "out"),
            Text("Name", "対象外 2"),
            Select("TaxationType", "out_of_scope"))));

        Assert.Contains("OUT", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("大文字と小文字を区別しない", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>自分自身は重複に数えない（コード以外を直すだけの保存を止めない）。</summary>
    [Fact]
    public async Task 自分と同じコードは重複に数えない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(
            server,
            Updating("Account", Row("Account", server.AccountOf("1100").Value, Text("Code", "1100")))));
    }

    /// <summary>補助科目は勘定科目ごとに数える（一意なのは組だから）。</summary>
    [Fact]
    public async Task 補助科目のコードは勘定科目ごとに数える()
    {
        using var server = new AccountingServer();
        var deposit = server.AccountOf("1210").Value;
        var other = server.AccountOf("1220").Value;

        // **関門を通さずに 1 件入れる。** 保存の本体を呼ばない形でテストしているので、
        // 関門を通した保存では行が残らない（それが分かる形にしてある）。
        server.InsertSubAccount("1210", "B1", "みずほ");

        // 別の勘定科目の下なら同じコードでよい
        Assert.True(await Submit(server, Adding("SubAccount", New(
            "SubAccount",
            Text("Code", "B1"),
            Text("Name", "みずほ"),
            ("Account", new LinkFieldData { Value = other.ToString(CultureInfo.InvariantCulture) })))));

        // 同じ勘定科目の下では断る
        var thrown = await Rejected(server, Adding("SubAccount", New(
            "SubAccount",
            Text("Code", "b1"),
            Text("Name", "みずほ 2"),
            ("Account", new LinkFieldData { Value = deposit.ToString(CultureInfo.InvariantCulture) }))));

        Assert.Contains("補助科目コード", thrown.Message, StringComparison.Ordinal);
    }

    // --- コードの書式（B-2） -----------------------------------------------------

    [Theory]
    [InlineData("11 00", "目に見えない文字")]
    [InlineData("１２００", "使えません")]
    [InlineData("-1200", "最初と最後")]
    [InlineData("12--00", "続けて")]
    [InlineData("123456789012345678901", "20 文字以内")]
    public async Task 書式に反するコードは利用者の語で断る(string code, string expected)
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(
            server, Adding("Account", New("Account", Text("Code", code), Text("Name", "検証"))));

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>前後の空白は落として保存する</b>（ADR-0047 の決定 5）。
    /// </summary>
    /// <remarks>
    /// <b>差分に書き戻す</b>ところまで見る。比べるときだけ落とすと、関門が通した値を
    /// DDL のトリガが拒む（関門の受理集合が DB より広い。qa/03 L-14 の型）。
    /// </remarks>
    [Fact]
    public async Task 前後の空白は落として差分に書き戻す()
    {
        using var server = new AccountingServer();
        var row = New("Account", Text("Code", "  9001  "), Text("Name", "検証"));

        Assert.True(await Submit(server, Adding("Account", row)));
        Assert.Equal("9001", ((TextFieldData)row.Fields["Code"]).Value);
    }

    [Fact]
    public async Task コードが空なら必須として断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(
            server, Adding("Account", New("Account", Text("Code", "   "), Text("Name", "検証"))));

        Assert.Contains("「科目コード」を入れてください", thrown.Message, StringComparison.Ordinal);
    }

    // --- 全社共通の部門（B-1 の 2 例目） -----------------------------------------

    /// <summary>
    /// 「全社共通」の部門は 1 つだけ。
    /// </summary>
    /// <remarks>
    /// <b>qa/03 L-28 が「とくに悪い」と書いた例である</b>——コードも名前も正しいのに拒まれ、
    /// 規則が画面のどこにも書いていないので、利用者には手がかりが無かった。
    /// </remarks>
    [Fact]
    public async Task 全社共通の部門は2つ目を断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Adding("Department", New(
            "Department",
            Text("Code", "90"),
            Text("Name", "全社共通 2"),
            ("IsCompanyWide", new BooleanFieldData { Value = true }))));

        Assert.Contains("「全社共通」の部門は 1 つだけです", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>いま「全社共通」の行が自分自身なら、オンのまま保存できる。</summary>
    [Fact]
    public async Task 自分が全社共通なら保存できる()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server, Updating("Department", Row(
            "Department",
            server.DepartmentOf("00").Value,
            ("IsCompanyWide", new BooleanFieldData { Value = true })))));
    }

    // --- 税区分の整合（B-1 の 4 例目） -------------------------------------------

    [Fact]
    public async Task 課税の区分に税率区分が無ければ断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Adding("TaxCategory", New(
            "TaxCategory",
            Text("Code", "TS2"),
            Text("Name", "課税売上 2"),
            Select("TaxationType", "taxable_sales"),
            Select("RateKind", null))));

        Assert.Contains("「税率区分」が要ります", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 課税でない区分に税率区分が付いていれば断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Adding("TaxCategory", New(
            "TaxCategory",
            Text("Code", "OUT2"),
            Text("Name", "対象外 2"),
            Select("TaxationType", "out_of_scope"),
            Select("RateKind", "standard"))));

        Assert.Contains("「税率区分」は付けられません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>片方だけ触った更新でも判定する。</b>
    /// </summary>
    /// <remarks>
    /// CLB は触った欄しか送らない（qa/01 F-12）ので、保存されている側を引いて組む。
    /// <b>これを見落とすと、課税の区分から税率区分だけを外せる。</b>
    /// </remarks>
    [Fact]
    public async Task 税率区分だけを外す更新も断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Updating("TaxCategory", Row(
            "TaxCategory", server.TaxCategoryOf("TS").Value, Select("RateKind", null))));

        Assert.Contains("「税率区分」が要ります", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>課税区分だけを触った更新も、保存されている税率区分と組んで判定する。</summary>
    [Fact]
    public async Task 課税区分だけを変える更新も断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Updating("TaxCategory", Row(
            "TaxCategory", server.TaxCategoryOf("TS").Value, Select("TaxationType", "out_of_scope"))));

        Assert.Contains("「税率区分」は付けられません", thrown.Message, StringComparison.Ordinal);
    }

    // --- 補助科目の 2 値（A-3 の残り） -------------------------------------------

    /// <summary>
    /// 補助科目を使わない勘定科目の下には作れない（ADR-0038 §3）。
    /// </summary>
    /// <remarks>
    /// <b>2026-09-08 の回では明細の側しか塞いでいなかった</b>——マスタの画面からは足せた。
    /// </remarks>
    [Fact]
    public async Task 補助科目を使わない科目の下には作れない()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Adding("SubAccount", New(
            "SubAccount",
            Text("Code", "S1"),
            Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = server.AccountOf("1100").Value.ToString(CultureInfo.InvariantCulture) }))));

        Assert.Contains("補助科目を使わない設定です", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 補助科目を使う科目の下には作れる()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server, Adding("SubAccount", New(
            "SubAccount",
            Text("Code", "S1"),
            Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = server.AccountOf("1210").Value.ToString(CultureInfo.InvariantCulture) })))));
    }

    /// <summary>
    /// 実在しない勘定科目は、ここでは止めない（外部キーが拒む）。
    /// </summary>
    /// <remarks>
    /// <b>実在の断りを 2 か所に置かない。</b> ここで止めると、DB の外部キーと同じことを
    /// 別の言い方で 2 回言うことになり、片方だけ直したときに食い違う。
    /// </remarks>
    [Fact]
    public async Task 実在しない勘定科目はこの関門では止めない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server, Adding("SubAccount", New(
            "SubAccount",
            Text("Code", "S9"),
            Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = "999999" })))));
    }

    // --- 関門の範囲 ---------------------------------------------------------------

    /// <summary>コードを持たないモジュールは素通りする。</summary>
    [Fact]
    public async Task コードを持たないモジュールは見ない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server, Adding("CompanyProfile", New("CompanyProfile", Text("Code", "1100")))));
    }

    /// <summary>コードを持つ 5 つのマスタを、この関門が見ている（docs/12 §2-1。取引先は取引先部品）。</summary>
    [Fact]
    public void 会計コードを持つ5つのマスタを見ている()
        => Assert.Equal(
            ["Account", "Department", "FiscalYear", "SubAccount", "TaxCategory"],
            MasterSubmitGate.Coded.Select(m => m.ModuleName).OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>新規作成の行（仮の識別子）を、更新として見分ける。</summary>
    [Fact]
    public void 仮の識別子は新規として見分ける()
    {
        var temporary = New("Account", Text("Code", "9999"));
        var stored = Row("Account", 1, Text("Code", "9999"));

        Assert.True(MasterSubmitGate.IsTemporary(temporary));
        Assert.False(MasterSubmitGate.IsTemporary(stored));
    }

    // --- 壊れた要求・触っていない欄（fail-safe の側） ------------------------------

    /// <summary>コードの欄が文字列でない要求は、コードを見ない（画面からは作れない形）。</summary>
    [Fact]
    public async Task コードの欄が文字列でなければ見ない()
    {
        using var server = new AccountingServer();
        var row = New("Account", ("Code", new BooleanFieldData { Value = true }), Text("Name", "検証"));

        Assert.True(await Submit(server, Adding("Account", row)));
    }

    /// <summary>「全社共通」を触っていない部門の保存は、全社共通を見ない。</summary>
    [Fact]
    public async Task 全社共通を触っていない部門の保存は見ない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(
            server, Updating("Department", Row("Department", server.DepartmentOf("10").Value, Text("Name", "総務課")))));
    }

    /// <summary>「全社共通」の欄が真偽でない要求は、全社共通を見ない。</summary>
    [Fact]
    public async Task 全社共通の欄が真偽でなければ見ない()
    {
        using var server = new AccountingServer();
        var row = New("Department", Text("Code", "91"), Text("Name", "検証"), Text("IsCompanyWide", "1"));

        Assert.True(await Submit(server, Adding("Department", row)));
    }

    /// <summary>課税区分も税率区分も触っていない税区分の保存は、整合を見ない。</summary>
    [Fact]
    public async Task 課税区分も税率区分も触っていなければ見ない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(
            server,
            Updating("TaxCategory", Row("TaxCategory", server.TaxCategoryOf("OUT").Value, Text("Name", "対象外（改）")))));
    }

    /// <summary>
    /// 保存されている行が見つからない更新は、整合を見ない。
    /// </summary>
    /// <remarks>
    /// <b>その保存は外部キーか楽観ロックが拒む</b>ので、ここで断りを重ねない
    /// （<c>MasterMeaningGate</c> が同じ形で素通しにしている）。
    /// </remarks>
    [Fact]
    public async Task 保存されている行が無い更新は整合を見ない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(
            server, Updating("TaxCategory", Row("TaxCategory", 999999, Select("RateKind", null)))));
    }

    /// <summary>識別子が読めない行は新規として扱う（仮の識別子の形が変わっても止めない）。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 識別子が読めない行は仮ではないと見分ける(string? id)
    {
        var data = new ModuleData { Name = "Account" };
        data.Fields["Id"] = new IdFieldData { Value = id };

        Assert.False(MasterSubmitGate.IsTemporary(data));
    }

    /// <summary>識別子の欄が無い行も、仮ではない。</summary>
    [Fact]
    public void 識別子の欄が無い行は仮ではない()
        => Assert.False(MasterSubmitGate.IsTemporary(new ModuleData { Name = "Account" }));

    /// <summary>識別子の欄が識別子でない行も、仮ではない。</summary>
    [Fact]
    public void 識別子の欄が識別子でなければ仮ではない()
    {
        var data = new ModuleData { Name = "Account" };
        data.Fields["Id"] = new TextFieldData { Value = "@temporary:1" };

        Assert.False(MasterSubmitGate.IsTemporary(data));
    }

    /// <summary>コードの欄が空（null）の追加も、必須として断る。</summary>
    [Fact]
    public async Task コードが_null_なら必須として断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(
            server, Adding("Account", New("Account", Text("Code", null), Text("Name", "検証"))));

        Assert.Contains("「科目コード」を入れてください", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 税率区分の欄が<b>そもそも届かない</b>追加も、整合を見る。
    /// </summary>
    /// <remarks>
    /// <b>「空で届く」と「届かない」は別である。</b> 新規は保存されている値が無いので、
    /// 届かない欄は空として判定するしかない。
    /// </remarks>
    [Fact]
    public async Task 税率区分が届かない追加も断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Adding("TaxCategory", New(
            "TaxCategory",
            Text("Code", "TS3"),
            Text("Name", "課税売上 3"),
            Select("TaxationType", "taxable_sales"))));

        Assert.Contains("「税率区分」が要ります", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>勘定科目を触っていない補助科目の保存は、2 値の規則を見ない。</summary>
    [Fact]
    public async Task 勘定科目を触っていない補助科目の保存は見ない()
    {
        using var server = new AccountingServer();
        var sub = server.InsertSubAccount("1210", "S001", "本店");

        Assert.True(await Submit(
            server, Updating("SubAccount", Row("SubAccount", sub, Text("Name", "本店（改）")))));
    }

    /// <summary>引数を渡さなければ止まる。<b>引数名まで表明する。</b></summary>
    [Fact]
    public async Task 引数を渡さなければ止まる()
    {
        using var server = new AccountingServer();
        var gate = MasterSubmitGate.Create(server.Accessor, TestSupport.SqliteDbAccessor.DataSourceName);

        var missingData = await Assert.ThrowsAsync<ArgumentNullException>(
            () => gate.SubmitAsync(null!, () => Task.FromResult(new List<ModuleSubmitResult>())));
        Assert.Equal("transactionData", missingData.ParamName);

        var missingSave = await Assert.ThrowsAsync<ArgumentNullException>(() => gate.SubmitAsync([], null!));
        Assert.Equal("save", missingSave.ParamName);

        Assert.Throws<ArgumentNullException>(() => MasterSubmitGate.IsTemporary(null!));
    }
}
