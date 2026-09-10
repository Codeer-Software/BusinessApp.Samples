namespace BusinessApp.AccountingCore.Server.Tests.Masters.Application;

using System.Globalization;

using BusinessApp.AccountingCore.Server.Tests.Fixtures;
using BusinessApp.ServerSupport;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Masters.Application;
using BusinessApp.AccountingCore.Server.Shared.Presentation;
using BusinessApp.AccountingCore.Server.Masters.Infrastructure;

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

    /// <summary>補助科目の親（勘定科目）を、コードから引いた識別子で指す。</summary>
    private static (string Field, FieldDataBase Value) Account(AccountingServer server, string code)
        => ("Account", new LinkFieldData
        {
            Value = server.AccountOf(code).Value.ToString(CultureInfo.InvariantCulture),
        });

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
        Assert.Contains("大文字と小文字を区別しないので", thrown.Message, StringComparison.Ordinal);
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
    [InlineData("-1200", "先頭に「-」「_」は置けません")]
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

        Assert.Contains("「税率区分」を選んでください", thrown.Message, StringComparison.Ordinal);
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

        Assert.Contains("「税率区分」は空にしてください", thrown.Message, StringComparison.Ordinal);
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

        Assert.Contains("「税率区分」を選んでください", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>課税区分だけを触った更新も、保存されている税率区分と組んで判定する。</summary>
    [Fact]
    public async Task 課税区分だけを変える更新も断る()
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Updating("TaxCategory", Row(
            "TaxCategory", server.TaxCategoryOf("TS").Value, Select("TaxationType", "out_of_scope"))));

        Assert.Contains("「税率区分」は空にしてください", thrown.Message, StringComparison.Ordinal);
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

        Assert.Contains("「補助科目を使う」がオフなので、補助科目を作れません", thrown.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// <b>写した表の名前と欄のラベルが、デザイン JSON と一致する</b>（docs/20 §4 の「已むを得ない重複」）。
    /// </summary>
    /// <remarks>
    /// <b>ラベルは差し戻しの文言に出る。</b> ずれると、画面に無い語を名指しして「どの欄のことか」が
    /// 分からなくなる（<c>MasterMeaningGateTests</c> が同じ形で守っている。2026-09-09 の自己レビューで、
    /// こちらに無いことを指摘された）。
    /// </remarks>
    [Fact]
    public void 写した表とラベルはデザインと一致する()
    {
        foreach (var master in MasterSubmitGate.Coded)
        {
            var path = Directory
                .EnumerateFiles(TestSupport.TestDatabase.ModulesDirectory, $"{master.ModuleName}.mod.json", SearchOption.AllDirectories)
                .Single();
            using var design = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

            Assert.Equal(master.Table, design.RootElement.GetProperty("DbTable").GetString());

            var code = design.RootElement.GetProperty("Fields").EnumerateArray()
                .Single(f => f.GetProperty("Name").GetString() == "Code");
            Assert.Equal("code", code.GetProperty("DbColumn").GetString());
            Assert.Equal(master.CodeLabel, code.GetProperty("DisplayName").GetString());

            if (master.Parent is CodedParent parent)
            {
                var link = design.RootElement.GetProperty("Fields").EnumerateArray()
                    .Single(f => f.GetProperty("Name").GetString() == parent.FieldName);
                Assert.Equal(parent.Column, link.GetProperty("DbColumn").GetString());
            }
        }
    }

    /// <summary>
    /// <b>関門が読む欄の型が、デザインで変わったらここで赤くなる。</b>
    /// </summary>
    /// <remarks>
    /// <para>関門は読めない型で止まるようにしてあるが、<b>止まるのは本番で保存された瞬間</b>である
    /// ——利用者に定型文が出て、直すべき側にはログが届くだけになる。
    /// <b>デザインに <c>TypeFullName</c> が入っている</b>ので、
    /// <b>型を変えた瞬間にコミット前で赤くする</b>ほうが早い（2026-09-09 の自己レビュー）。</para>
    /// <para><b>デザインの型と、関門が期待するデータの型は名前で対応する</b>——
    /// <c>TextFieldDesign</c> の値は <c>TextFieldData</c> で届く。</para>
    /// </remarks>
    /// <summary>
    /// コードと親の欄の型は、<see cref="MasterSubmitGate.Coded"/> から回して確かめる。
    /// </summary>
    /// <remarks>
    /// <b>一覧を手で保つと、マスタが増えた日に行を足し忘れる</b>（2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public void コードと親の欄の型はデザインと一致する()
    {
        foreach (var master in MasterSubmitGate.Coded)
        {
            関門が読む欄の型はデザインと一致する(master.ModuleName, "Code", "TextFieldDesign");

            if (master.Parent is CodedParent parent)
            {
                関門が読む欄の型はデザインと一致する(master.ModuleName, parent.FieldName, "LinkFieldDesign");
            }
        }
    }

    [Theory]
    [InlineData("Department", "IsCompanyWide", "BooleanFieldDesign")]
    [InlineData("TaxCategory", "TaxationType", "SelectFieldDesign")]
    [InlineData("TaxCategory", "RateKind", "SelectFieldDesign")]
    public void 関門が読む欄の型はデザインと一致する(string module, string field, string designType)
    {
        var path = Directory
            .EnumerateFiles(TestSupport.TestDatabase.ModulesDirectory, $"{module}.mod.json", SearchOption.AllDirectories)
            .Single();
        using var design = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

        var found = design.RootElement.GetProperty("Fields").EnumerateArray()
            .Single(f => f.GetProperty("Name").GetString() == field);

        Assert.Equal(
            $"Codeer.LowCode.Blazor.Repository.Design.{designType}",
            found.GetProperty("TypeFullName").GetString());
    }

    /// <summary>コードを持つ 5 つのマスタを、この関門が見ている（docs/12 §2-1。取引先は取引先部品）。</summary>
    [Fact]
    public void 会計コードを持つ5つのマスタを見ている()
        => Assert.Equal(
            ["Account", "Department", "FiscalYear", "SubAccount", "TaxCategory"],
            MasterSubmitGate.Coded.Select(m => m.ModuleName).OrderBy(n => n, StringComparer.Ordinal));

    // --- 更新の経路（差分しか届かない。qa/01 F-12） ------------------------------

    /// <summary>
    /// <b>補助科目のコードだけを直す更新でも、同じ勘定科目の下の重複を見る。</b>
    /// </summary>
    /// <remarks>
    /// <b>2026-09-09 の自己レビューで見つけた穴である。</b> 親（勘定科目）は差分に載らないので、
    /// 補わずに照会すると <c>account_id = NULL</c> が 1 行も返さず、関門が素通りしていた
    /// ——最後は一意索引が拒み、利用者には定型文が出る（qa/03 L-28 に戻る）。
    /// </remarks>
    [Fact]
    public async Task 補助科目のコードだけを直す更新でも重複を見る()
    {
        using var server = new AccountingServer();
        server.InsertSubAccount("1210", "B1", "みずほ");
        var target = server.InsertSubAccount("1210", "B2", "三井");

        var thrown = await Rejected(
            server, Updating("SubAccount", Row("SubAccount", target, Text("Code", "b1"))));

        Assert.Contains("補助科目コード", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("B1", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>コードを触らずに勘定科目だけを移した更新でも、移した先の重複を見る。</b>
    /// </summary>
    /// <remarks>
    /// <b>一意なのは（勘定科目, コード）の組なので、コードが 1 字も変わらなくても
    /// 親が動けば重複になり得る。</b> 差分にはコードが載らない（qa/01 F-12）から、
    /// <b>保存されている字を読み直して数える</b>（2026-09-09 の自己レビュー。
    /// 親を読み直す穴と同じ家系で、こちらだけ残っていた）。
    /// </remarks>
    [Fact]
    public async Task 勘定科目だけを移した更新でも移した先の重複を見る()
    {
        using var server = new AccountingServer();
        server.InsertSubAccount("1220", "B1", "みずほ（定期）");
        var target = server.InsertSubAccount("1210", "b1", "みずほ（当座）");

        var thrown = await Rejected(
            server,
            Updating("SubAccount", Row("SubAccount", target, Account(server, "1220"))));

        Assert.Equal(
            "登録できません。「補助科目コード」の「b1」は大文字と小文字を区別しないので、"
            + "この勘定科目の中では既にある「B1」と同じコードになります。"
            + "別の勘定科目を選ぶか、コードを変えてください。",
            thrown.Message);
    }

    /// <summary>大小まで同じコードで移したときは、相手の字を見せる代わりに「既に使われています」と言う。</summary>
    /// <remarks>
    /// <b>大小が違うときだけ「区別しないので」と説明する。</b> 同じ字なのにその説明を出すと、
    /// 利用者は「同じに見えるのに何が違うのか」と読む（<c>string.Equals</c> の枝）。
    /// </remarks>
    [Fact]
    public async Task 大小まで同じコードで移したときは相手の字を見せない()
    {
        using var server = new AccountingServer();
        server.InsertSubAccount("1220", "B1", "みずほ（定期）");
        var target = server.InsertSubAccount("1210", "B1", "みずほ（当座）");

        var thrown = await Rejected(
            server,
            Updating("SubAccount", Row("SubAccount", target, Account(server, "1220"))));

        Assert.Equal(
            "登録できません。「補助科目コード」の「B1」はこの勘定科目の中では既に使われています。"
            + "別の勘定科目を選ぶか、コードを変えてください。",
            thrown.Message);
    }

    /// <summary>移した先が空いていれば通る（<b>移動そのものを止めない</b>）。</summary>
    [Fact]
    public async Task 移した先にぶつかるコードが無ければ通る()
    {
        using var server = new AccountingServer();
        var target = server.InsertSubAccount("1210", "B1", "みずほ");

        Assert.True(await Submit(
            server,
            Updating("SubAccount", Row("SubAccount", target, Account(server, "1220")))));
    }

    /// <summary>
    /// <b>欄が読めない型で届いたら、素通しではなく止める。</b>
    /// </summary>
    /// <remarks>
    /// <c>_ =&gt; null</c> で帰る形だと「触られていない」と見分けがつかず、
    /// <b>デザインで欄の型を変えた日に検査が黙って素通しへ落ちる</b>のに、
    /// フィクスチャが自分で正しい型を組むのでテストは緑のままである（2026-09-09 の自己レビュー）。
    /// <b>利用者向けの断りではない</b>——直すのは利用者ではなく、デザインを変えた側だからである。
    /// </remarks>
    [Fact]
    public async Task 読めない型で届いた欄は素通ししないで止める()
    {
        using var server = new AccountingServer();
        var data = New("Account", Text("Name", "現金その 2"));
        data.Fields["Code"] = new NumberFieldData { Value = 1100 };

        await AssertUnreadable(server, Adding("Account", data), "Code", "NumberFieldData");
    }

    /// <summary>
    /// 同じ保存の中で作る勘定科目を指す補助科目は、親を数値として読めない（仮の識別子）。
    /// </summary>
    /// <remarks>
    /// <b>これは正常な形である</b>（qa/01 C-08）。<b>読めない型とは違う</b>ので止めない——
    /// 同じ保存に親が居なければ 2 値も判定できないので、重複は DB の一意索引が見る。
    /// </remarks>
    [Fact]
    public async Task 仮の識別子の親を指す補助科目の追加は通る()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server, Adding("SubAccount", New(
            "SubAccount", Text("Code", "S9"), Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = "@temporary:2" })))));
    }

    /// <summary>
    /// <b>科目と補助科目を同じ保存で作るときも、2 値の規則を見る。</b>
    /// </summary>
    /// <remarks>
    /// 親はまだ DB に無い（仮の識別子）ので、<b>同じ保存の中から探す</b>。
    /// <b>この規則には DB 側の受け皿が無い</b>（<c>uses_sub_account</c> を見るトリガは明細の側だけ）ので、
    /// ここを素通しにすると ADR-0038 §3 の 2 値が取込・API では丸ごと消える
    /// （2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 同じ保存で作る科目が補助科目を使わないなら断る()
    {
        using var server = new AccountingServer();

        var account = New("Account", Text("Code", "9300"), Text("Name", "検証"), Select("Category", "asset"));
        account.Fields["Id"] = new IdFieldData { Value = "@temporary:7" };
        account.Fields["UsesSubAccount"] = new BooleanFieldData { Value = false };

        var sub = New("SubAccount", Text("Code", "S9"), Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = "@temporary:7" }));

        var thrown = await Rejected(
            server,
            new ModuleSubmitData { ModuleName = "Account", Add = [account, sub] });

        Assert.Contains("「補助科目を使う」がオフなので、補助科目を作れません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>勘定科目の欄が空の補助科目は、2 値を判定できない（外部キーが断る）。</summary>
    [Fact]
    public async Task 勘定科目が空の補助科目は2値を見ない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server, Adding("SubAccount", New(
            "SubAccount", Text("Code", "S9"), Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = string.Empty })))));
    }

    /// <summary>同じ保存に居る科目に識別子が無ければ、親として見つけられない。</summary>
    /// <remarks>
    /// <b>仮の識別子どうしを突き合わせる</b>ので、識別子の無い行は結び付けようがない。
    /// 画面からは起きない（<c>Id</c> は常に来る。qa/01 F-12）。
    /// </remarks>
    [Fact]
    public async Task 同じ保存に居る科目に識別子が無ければ結び付かない()
    {
        using var server = new AccountingServer();

        var account = new ModuleData { Name = "Account" };
        account.Fields["Code"] = new TextFieldData { Value = "9300" };
        account.Fields["Name"] = new TextFieldData { Value = "検証" };
        account.Fields["Category"] = new SelectFieldData { Value = "asset" };
        account.Fields["UsesSubAccount"] = new BooleanFieldData { Value = false };

        var sub = New("SubAccount", Text("Code", "S9"), Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = "@temporary:7" }));

        Assert.True(await Submit(
            server, new ModuleSubmitData { ModuleName = "Account", Add = [sub, account] }));
    }

    /// <summary>同じ保存に居る科目の識別子が読めない型なら止める。</summary>
    /// <remarks>
    /// <b>自分の行より後ろの行も見る</b>ので、その行の <c>Id</c> をまだ検査していないことがある。
    /// 黙って読み飛ばすと、<b>親が居るのに「居ない」と判断して 2 値の規則が消える</b>。
    /// </remarks>
    [Fact]
    public async Task 同じ保存に居る科目の識別子が読めない型なら止める()
    {
        using var server = new AccountingServer();

        var account = New("Account", Text("Code", "9300"), Text("Name", "検証"), Select("Category", "asset"));
        account.Fields["Id"] = new NumberFieldData { Value = 7 };

        var sub = New("SubAccount", Text("Code", "S9"), Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = "@temporary:7" }));

        await AssertUnreadable(
            server,
            new ModuleSubmitData { ModuleName = "Account", Add = [sub, account] },
            "Id",
            "NumberFieldData");
    }

    /// <summary>同じ保存で作る科目が「補助科目を使う」なら通る。</summary>
    [Fact]
    public async Task 同じ保存で作る科目が補助科目を使うなら通る()
    {
        using var server = new AccountingServer();

        var account = New("Account", Text("Code", "9300"), Text("Name", "検証"), Select("Category", "asset"));
        account.Fields["Id"] = new IdFieldData { Value = "@temporary:7" };
        account.Fields["UsesSubAccount"] = new BooleanFieldData { Value = true };

        var sub = New("SubAccount", Text("Code", "S9"), Text("Name", "検証"),
            ("Account", new LinkFieldData { Value = "@temporary:7" }));

        Assert.True(await Submit(
            server, new ModuleSubmitData { ModuleName = "Account", Add = [account, sub] }));
    }

    /// <summary>更新で親が仮の識別子なら、保存されている親で数える。</summary>
    /// <remarks>
    /// <b>読めない値のときに「範囲なし」へ落とさない</b>——落とすと重複の照会が
    /// <c>account_id = NULL</c> になり、1 行も返さずに素通りする（qa/03 L-28 に戻る）。
    /// </remarks>
    [Fact]
    public async Task 更新で親が仮の識別子なら保存されている親で数える()
    {
        using var server = new AccountingServer();
        server.InsertSubAccount("1210", "B1", "みずほ");
        var target = server.InsertSubAccount("1210", "B2", "三井");

        var thrown = await Rejected(server, Updating("SubAccount", Row(
            "SubAccount", target,
            Text("Code", "b1"),
            ("Account", new LinkFieldData { Value = "@temporary:2" }))));

        Assert.Contains("既にある「B1」", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 更新の側に混ざった仮の識別子の行は、保存されているコードを読み直せない。
    /// </summary>
    /// <remarks>
    /// <b>CLB は新しく作った子を更新の側に載せることがある</b>
    /// （<c>MasterMeaningGateTests.更新の側に混ざった仮の識別子の行は通す</c> と同じ形）。
    /// その行はまだ DB に無いので、<b>読み直す先が無い</b>——重複は DB の一意索引が見る。
    /// </remarks>
    [Fact]
    public async Task 更新に混ざった仮の識別子の行は読み直さない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server, Updating("SubAccount", New(
            "SubAccount", ("Account", new LinkFieldData { Value = "@temporary:2" })))));
    }

    /// <summary>
    /// 識別子の欄が届かない要求は、新規として扱う。
    /// </summary>
    /// <remarks>
    /// <b>画面からも CLB からも起きない</b>（<c>Id</c> と <c>OptimisticLocking</c> は常に来る。qa/01 F-12）ので、
    /// これは取込・API を直に叩いた経路だけの形である。<b>新規の側へ倒すのは、
    /// 更新として扱うと「自分自身」を除く相手が決まらず、重複の照会が意味を失うから</b>——
    /// 新規として数えれば、既にあるコードは断り、無ければ最後に DB の一意索引が受け止める。
    /// </remarks>
    [Fact]
    public async Task 識別子の欄が届かなければ新規として扱う()
    {
        using var server = new AccountingServer();
        var row = new ModuleData { Name = "Department" };
        row.Fields["Code"] = new TextFieldData { Value = "92" };
        row.Fields["Name"] = new TextFieldData { Value = "検証" };

        Assert.True(await Submit(server, Adding("Department", row)));

        // **新規として数えている**ことを、既にあるコードで確かめる（自分自身を除いていない）。
        var duplicate = new ModuleData { Name = "Department" };
        duplicate.Fields["Code"] = new TextFieldData { Value = "10" };

        Assert.Contains(
            "既に使われています",
            (await Rejected(server, Adding("Department", duplicate))).Message,
            StringComparison.Ordinal);
    }

    /// <summary>識別子の欄が読めない型なら止める（新規として扱わない）。</summary>
    /// <remarks>
    /// <para>黙って <c>null</c> に落とすと<b>更新が新規として扱われ、自分自身を重複と誤って断る</b>向きに倒れる。</para>
    /// <para><b>検体に会計年度を選ぶ。</b> 意味の凍結の関門（<c>MasterMeaningGate</c>）は
    /// 先に走って「登録する行を特定できませんでした」と<b>利用者の語で</b>断るが、
    /// <b>会計年度はそちらの対象ではない</b>（計上済みの明細から参照されない）。
    /// つまりここが<b>唯一の守り</b>になるマスタである。</para>
    /// </remarks>
    [Fact]
    public async Task 識別子の欄が読めない型なら止める()
    {
        using var server = new AccountingServer();
        var row = new ModuleData { Name = "FiscalYear" };
        row.Fields["Id"] = new NumberFieldData { Value = 1 };
        row.Fields["Code"] = new TextFieldData { Value = "FY99" };

        await AssertUnreadable(server, Updating("FiscalYear", row), "Id", "NumberFieldData");
    }

    /// <summary>原文の受け皿を繋がない配線でも、利用者への文言は同じ。</summary>
    /// <remarks>
    /// 受け皿は任意なので（<c>AccountingSubmitPipeline.Create</c> の既定は「何もしない」）、
    /// <b>繋ぎ忘れたときに文言まで変わらないこと</b>を見る。
    /// </remarks>
    [Fact]
    public async Task 受け皿を繋がなくても利用者への文言は同じ()
    {
        using var server = new AccountingServer();
        var data = New("Account", Text("Name", "検証"));
        data.Fields["Code"] = new NumberFieldData { Value = 1100 };

        var results = await server.PipelineWithoutLog.SubmitAsResultAsync(
            [Adding("Account", data)], () => Task.FromResult(new List<ModuleSubmitResult>()));

        Assert.Equal([SaveFailureMessageText], results.Select(r => r.ExceptionMessage));
    }

    /// <summary>
    /// 読めない型で止まったとき、<b>利用者には定型文・原文はログへ</b>回っていることを見る。
    /// </summary>
    /// <remarks>
    /// <b>利用者の誤りではない</b>ので、欄の名前も CLB の型名も画面に出さない（docs/21 §2-2）。
    /// <b>同時に、直すべき側には届かせる</b>——ホストの例外ハンドラは <c>Message</c> を
    /// そのまま返すので、投げたままだと内部表現が画面に出る（2026-09-09 の自己レビュー）。
    /// </remarks>
    private static async Task AssertUnreadable(
        AccountingServer server, ModuleSubmitData submitted, string field, string typeName)
    {
        var called = false;
        var results = await server.Pipeline.SubmitAsResultAsync([submitted], () =>
        {
            called = true;
            return Task.FromResult(new List<ModuleSubmitResult>());
        });

        var message = Assert.Single(results).ExceptionMessage;
        Assert.Equal(SaveFailureMessageText, message);
        Assert.DoesNotContain(field, message, StringComparison.Ordinal);
        Assert.DoesNotContain(typeName, message, StringComparison.Ordinal);

        var logged = Assert.Single(server.SaveFailureLog);
        Assert.Contains(field, logged, StringComparison.Ordinal);
        Assert.Contains(typeName, logged, StringComparison.Ordinal);
        Assert.False(called);
    }

    /// <summary>
    /// 保存が失敗したときの定型文。
    /// </summary>
    /// <remarks>
    /// <b>写しである</b>（<c>SaveFailureMessage</c> は <c>internal</c>）。
    /// 食い違ったらこのテストが赤くなるので、写しが古くなったままにはならない。
    /// </remarks>
    private const string SaveFailureMessageText =
        "保存できませんでした。入力内容を確かめ、画面を開き直してもう一度お試しください。"
        + "同じことが続くときは、管理者にお知らせください。";

    /// <summary>コードも勘定科目も触らない補助科目の更新は、数え直さない。</summary>
    /// <remarks><b>読み直しは親が動いたときだけ</b>——名前を直すだけの保存で毎回引かない。</remarks>
    [Fact]
    public async Task コードも勘定科目も触らない補助科目の更新は数え直さない()
    {
        using var server = new AccountingServer();
        var target = server.InsertSubAccount("1210", "B1", "みずほ");

        Assert.True(await Submit(
            server, Updating("SubAccount", Row("SubAccount", target, Text("Name", "みずほ銀行")))));
    }

    /// <summary>保存されている行が無い補助科目を移す更新は、読める字が無いので数えない（fail-safe）。</summary>
    [Fact]
    public async Task 保存されている行が無い補助科目を移す更新は数えない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(
            server, Updating("SubAccount", Row("SubAccount", 999999, Account(server, "1220")))));
    }

    /// <summary>
    /// <b>コードの無い追加は断る。</b>
    /// </summary>
    /// <remarks>
    /// <b>画面は必ずコードを送るが、取込は列ごと落とせる</b>（`code` の無い CSV）——
    /// <b>取込こそこの関門が守る経路である</b>。素通しにすると DB の <c>NOT NULL</c> に当たり、
    /// 利用者には定型文が出る（qa/03 L-28 に戻る）。
    /// <b>この形を「通る」と表明する検体が置かれていた</b>（2026-09-09 の自己レビュー）。
    /// </remarks>
    [Theory]
    [InlineData("SubAccount", "補助科目コード")]
    [InlineData("Account", "科目コード")]
    [InlineData("Department", "部門コード")]
    [InlineData("TaxCategory", "税区分コード")]
    [InlineData("FiscalYear", "年度コード")]
    public async Task コードの無い追加は断る(string module, string label)
    {
        using var server = new AccountingServer();

        var thrown = await Rejected(server, Adding(module, New(module, Text("Name", "検証"))));

        Assert.EndsWith($"「{label}」を入れてください。", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>親を触らない更新でも、補助科目の 2 値を見る（同じ穴の裏側）。</summary>
    [Fact]
    public async Task 親を触らない補助科目の更新でも2値を見る()
    {
        using var server = new AccountingServer();
        // 規則より前に作られた行を模す（画面からは作れない）。
        var target = server.InsertSubAccount("1100", "S1", "規則より前の補助科目");

        var thrown = await Rejected(
            server, Updating("SubAccount", Row("SubAccount", target, Text("Name", "改名"))));

        Assert.Contains("「補助科目を使う」がオフなので、補助科目を作れません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 参照の欄が <c>IdFieldData</c> で届いても、親として読める。
    /// </summary>
    /// <remarks>
    /// <b>型を 1 つに決め打ちしない</b>（<c>PartnerSubmitGate.Reference</c> が名指しで戒めている形）。
    /// 決め打ちにすると、フィールドの型が変わった日に検査が黙って素通しに落ち、
    /// フィクスチャが自分で同じ型を組むのでテストは緑のままになる。
    /// </remarks>
    [Fact]
    public async Task 参照の欄が別の型で届いても親として読める()
    {
        using var server = new AccountingServer();
        var cash = server.AccountOf("1100").Value.ToString(CultureInfo.InvariantCulture);

        var thrown = await Rejected(server, Adding("SubAccount", New(
            "SubAccount",
            Text("Code", "S2"),
            Text("Name", "検証"),
            ("Account", new IdFieldData { Value = cash }))));

        Assert.Contains("「補助科目を使う」がオフなので、補助科目を作れません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>関門が通した値は、DB も受け取れる</b>（往復。qa/03 L-14 の処方）。
    /// </summary>
    /// <remarks>
    /// 関門の受理集合が DB より広いと、「画面は通すのに保存で落ちる」という壊れ方をする。
    /// <b>差分に書き戻した姿</b>をそのまま DB へ流して確かめる。
    /// </remarks>
    [Theory]
    [InlineData("  9001  ", "9001")]
    [InlineData("A-1_2", "A-1_2")]
    [InlineData("12345678901234567890", "12345678901234567890")]
    public async Task 関門が通した値は_DB_も受け取れる(string typed, string stored)
    {
        using var server = new AccountingServer();
        var row = New("Account", Text("Code", typed), Text("Name", "検証"));

        Assert.True(await Submit(server, Adding("Account", row)));

        var normalized = ((TextFieldData)row.Fields["Code"]).Value;
        server.Execute($"insert into accounts (code, name, category) values ('{normalized}', '検証', 'asset')");

        Assert.Equal(stored, server.Scalar<string>($"select code from accounts where name = '検証'"));
    }

    /// <summary>
    /// <b>関門が通した「移動」を、DB も受け取れる</b>（往復。qa/03 L-14 の処方）。
    /// </summary>
    /// <remarks>
    /// <b>移動の争点は、関門の受理集合が <c>UNIQUE (account_id, code COLLATE NOCASE)</c> に
    /// 収まっているかである。</b> 偽の <c>save</c> が呼ばれたことだけを見ても、それは分からない
    /// ——**実際に <c>UPDATE</c> を流して読み戻す**（2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 関門が通した移動は_DB_も受け取れる()
    {
        using var server = new AccountingServer();
        var target = server.InsertSubAccount("1210", "B1", "みずほ");
        var destination = server.AccountOf("1220").Value;

        // **移した先に別のコードを置く。** 空の科目へ移すと
        // `UNIQUE (account_id, code COLLATE NOCASE)` が鳴りようがなく、
        // 「関門の受理集合が DB に収まるか」を 1 文字も表明できない（2026-09-09 の自己レビュー）。
        server.InsertSubAccount("1220", "B2", "三井");

        Assert.True(await Submit(
            server, Updating("SubAccount", Row("SubAccount", target, Account(server, "1220")))));

        server.Execute($"update sub_accounts set account_id = {destination} where id = {target}");

        Assert.Equal(
            destination,
            server.Scalar<long>($"select account_id from sub_accounts where id = {target}"));
        Assert.Equal(2L, server.Scalar<long>($"select count(*) from sub_accounts where account_id = {destination}"));
    }

    /// <summary>
    /// <b>関門が断った移動は、DB も断る</b>（受理集合の逆向き。qa/03 L-14 の処方）。
    /// </summary>
    /// <remarks>
    /// 関門が DB より<b>狭い</b>と、画面が断るのに取込は通る（守りが 1 層に落ちる）。
    /// <b>関門を外したときに何が起きるか</b>を、同じ <c>UPDATE</c> で確かめる。
    /// </remarks>
    [Fact]
    public async Task 関門が断った移動は_DB_も断る()
    {
        using var server = new AccountingServer();
        var target = server.InsertSubAccount("1210", "b1", "みずほ（当座）");
        server.InsertSubAccount("1220", "B1", "みずほ（定期）");
        var destination = server.AccountOf("1220").Value;

        await Rejected(server, Updating("SubAccount", Row("SubAccount", target, Account(server, "1220"))));

        var thrown = Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(
            () => server.Execute($"update sub_accounts set account_id = {destination} where id = {target}"));

        Assert.Contains("UNIQUE constraint failed", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("sub_accounts", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 参照の欄が読めない型で届いた追加は、親を読まずに通す（DB の外部キーが拒む）。
    /// </summary>
    /// <remarks>
    /// <b>ここで断りを重ねない。</b> 実在の判定は DB の外部キー 1 か所に置いてある
    /// （<see cref="実在しない勘定科目はこの関門では止めない"/> と同じ分担）。
    /// </remarks>
    [Fact]
    public async Task 参照の欄が読めない型なら止める()
    {
        using var server = new AccountingServer();

        await AssertUnreadable(
            server,
            Adding("SubAccount", New(
                "SubAccount", Text("Code", "S3"), Text("Name", "検証"),
                ("Account", new BooleanFieldData { Value = true }))),
            "Account",
            "BooleanFieldData");
    }

    /// <summary>保存されている行が無い補助科目の更新は、親を読めないので重複も 2 値も見ない。</summary>
    [Fact]
    public async Task 保存されている行が無い補助科目の更新は親を読めない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(
            server, Updating("SubAccount", Row("SubAccount", 999999, Text("Code", "S4")))));
    }

    // --- 壊れた要求・触っていない欄（fail-safe の側） ------------------------------

    /// <summary>「全社共通」を触っていない部門の保存は、全社共通を見ない。</summary>
    [Fact]
    public async Task 全社共通を触っていない部門の保存は見ない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(
            server, Updating("Department", Row("Department", server.DepartmentOf("10").Value, Text("Name", "総務課")))));
    }

    /// <summary>「全社共通」の欄が真偽でなければ止める（素通ししない）。</summary>
    /// <remarks>
    /// 黙って <c>null</c> を返すと、<b>「全社共通」の 2 件目の検査だけが丸ごと素通し</b>になる
    /// ——欄の型を変えた日に、この規則だけが消える（2026-09-09 の自己レビュー）。
    /// </remarks>
    [Fact]
    public async Task 全社共通の欄が真偽でなければ止める()
    {
        using var server = new AccountingServer();
        var row = New("Department", Text("Code", "91"), Text("Name", "検証"), Text("IsCompanyWide", "1"));

        await AssertUnreadable(server, Adding("Department", row), "IsCompanyWide", "TextFieldData");
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

        Assert.Contains("「税率区分」を選んでください", thrown.Message, StringComparison.Ordinal);
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
    }
}
