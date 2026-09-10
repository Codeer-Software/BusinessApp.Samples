namespace BusinessApp.AccountingCore.Server.Tests.Masters.Application;

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Masters.Application;
using BusinessApp.AccountingCore.Server.Masters.Infrastructure;

/// <summary>
/// 使用中のマスタは意味を変えられない（ADR-0038）。<b>関門の側。</b>
/// </summary>
/// <remarks>
/// <para><b>差分は実機の形で組む</b>（qa/01 F-11・F-12）——更新は識別子と触った欄だけが届く。
/// 全フィールドが揃った都合のよい形で書くと、実機で通らない経路を検査したことになる。</para>
/// <para>保存の本体（<c>save</c>）が呼ばれたかどうかで「通した／止めた」を見る。</para>
/// </remarks>
public class MasterMeaningGateTests
{
    private static ModuleSubmitData Updating(string module, ModuleData data)
        => new() { ModuleName = module, Update = [data] };

    private static ModuleSubmitData Adding(string module, ModuleData data)
        => new() { ModuleName = module, Add = [data] };

    private static ModuleData Row(string module, string id, string field, FieldDataBase value)
        => Row(module, id, (field, value));

    private static ModuleData Row(string module, string id, params (string Field, FieldDataBase Value)[] fields)
    {
        var data = new ModuleData { Name = module };
        data.Fields["Id"] = new IdFieldData { Value = id };
        foreach (var (field, value) in fields)
        {
            data.Fields[field] = value;
        }

        return data;
    }

    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>保存を通したか。止めたときは例外が飛ぶ。</summary>
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

    /// <summary>未払金 1,000 ／ 現金 1,000（未払金を現金で払う）を計上する。<b>現金・未払金・対象外の税区分が「使用中」になる。</b></summary>
    private static void PostPayment(AccountingServer server, int entryNo = 1)
        => server.InsertPosted(entryNo, "支払", "2026-08-24", ("debit", "2200", 1000), ("credit", "1100", 1000));

    // --- 止める -------------------------------------------------------------------

    /// <summary>
    /// <b>qa/03 L-29 そのもの。</b> 計上済みの明細が使う科目の科目区分は変えられない。
    /// 文言は「何件あるか」と「次に何をすればよいか」を持つ（ADR-0038 §4）。
    /// </summary>
    [Fact]
    public async Task 使用中の科目の科目区分を変えると差し戻される()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var cash = Id(server.AccountOf("1100").Value);

        var thrown = await Rejected(server,
            Updating("Account", Row("Account", cash, "Category", new SelectFieldData { Value = "expense" })));

        Assert.Equal(
            "登録できません。この勘定科目は計上済みの仕訳明細 1 行で使われているので、「科目区分」は変えられません。"
            + "新しい勘定科目を作って、以後の振替伝票ではそちらを選んでください。",
            thrown.Message);
    }

    /// <summary>
    /// <b>「取引先を要する」をオフにすると差し戻される</b>（docs/10 §6-2。<b>一方通行の列</b>）。
    /// </summary>
    /// <remarks>
    /// <b>止めないと、二層の守りをまとめて外せる</b>——オフにして計上し、また戻せば、
    /// 計上の関門も DDL のトリガも素通りする（自己レビューで見つけた。2026-09-08。qa/03 L-08 の型）。
    /// <b>文言は「変えられません」ではない</b>——オンにするのはいつでも通るからである。
    /// </remarks>
    [Fact]
    public async Task 使用中の科目の取引先の必須は外せない()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var payable = Id(server.AccountOf("2200").Value);
        server.Execute("update accounts set requires_partner = 1 where code = '2200'");

        var thrown = await Rejected(server,
            Updating("Account", Row("Account", payable, "RequiresPartner", new BooleanFieldData { Value = false })));

        Assert.Equal(
            "登録できません。この勘定科目は計上済みの仕訳明細 1 行で使われているので、「取引先を要する」をオフにできません。"
            + "オフにしている間に計上した明細は、取引先が空のまま帳簿に残ってしまいます。"
            + "この勘定科目を使う明細には「取引先」を選んでください。",
            thrown.Message);
    }

    /// <summary>
    /// <b>厳しくする向き（オフ → オン）は、使用中でも通る。</b>
    /// </summary>
    /// <remarks>
    /// <b>ここが赤くなったら、規則を後から採り入れられなくしている</b>——
    /// 立てたいのは計上済みの明細がある科目（売掛金・買掛金）である（docs/10 §6-2）。
    /// </remarks>
    [Fact]
    public async Task 使用中の科目でも取引先を必須にはできる()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var payable = Id(server.AccountOf("2200").Value);

        Assert.True(await Submit(server,
            Updating("Account", Row("Account", payable, "RequiresPartner", new BooleanFieldData { Value = true }))));
    }

    /// <summary>
    /// <b>使っていない科目なら、オフにできる</b>（「使用中」の線は ADR-0038 §1 と同じ）。
    /// </summary>
    [Fact]
    public async Task 使っていない科目なら取引先の必須を外せる()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        // 売掛金は初期データで requires_partner = 1 だが、計上済みの明細は無い。
        // **「使用中か」を先に見るので、一方通行の判定には入らない**（早期 return）。
        // DDL の側で同じことを見るのは MasterMeaningGuardTests。
        var receivable = Id(server.AccountOf("1300").Value);

        Assert.True(await Submit(server,
            Updating("Account", Row("Account", receivable, "RequiresPartner", new BooleanFieldData { Value = false }))));
    }

    /// <summary>
    /// 明細の数は伝票ではなく<b>明細</b>で数える（ADR-0017）。
    /// <b>1 伝票に現金の明細が 2 行</b>——伝票で数えると 1、明細で数えると 2 になる形で撃ち分ける。
    /// </summary>
    [Fact]
    public async Task 件数は伝票ではなく仕訳明細で数える()
    {
        using var server = new AccountingServer();
        server.InsertPosted(1, "分けて払う", "2026-08-24",
            ("debit", "2200", 1000), ("credit", "1100", 600), ("credit", "1100", 400));
        var cash = Id(server.AccountOf("1100").Value);

        var thrown = await Rejected(server,
            Updating("Account", Row("Account", cash, "Code", new TextFieldData { Value = "1101" })));

        Assert.Contains("仕訳明細 2 行で使われているので、「科目コード」は変えられません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>件数は 3 桁区切りで書く（金額と同じ読み方。年間数千伝票のペルソナでは 4 桁になる）。</summary>
    [Fact]
    public async Task 件数は3桁区切りで書く()
    {
        using var server = new AccountingServer();
        var entry = server.InsertDraft();
        server.Execute($"""
            with recursive n(i) as (select 1 union all select i + 1 from n where i < 1000)
            insert into journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            select {entry.Value}, i, case when i % 2 = 0 then 'debit' else 'credit' end,
                   (select id from accounts where code = '1100'), 100, (select id from tax_categories where code = 'OUT')
              from n;
            update journal_entries set status = 'posted', entry_no = 1, posted_at = '2026-08-24 13:00:00' where id = {entry.Value};
            """);

        var thrown = await Rejected(server,
            Updating("Account", Row("Account", Id(server.AccountOf("1100").Value), "Code", new TextFieldData { Value = "1101" })));

        Assert.Contains("仕訳明細 1,000 行で使われているので", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>触った列が複数なら、全部を並べて 1 回で断る（直しては弾かれを繰り返させない）。<b>並びは画面の並び。</b></summary>
    [Fact]
    public async Task 変えた列を全部並べて断る()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var cash = Row("Account", Id(server.AccountOf("1100").Value), "Category", new SelectFieldData { Value = "expense" });
        cash.Fields["IsContra"] = new BooleanFieldData { Value = true };
        cash.Fields["UsesSubAccount"] = new BooleanFieldData { Value = true };

        var thrown = await Rejected(server, Updating("Account", cash));

        Assert.Contains("「科目区分」・「補助科目を使う」・「評価勘定」は変えられません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>税区分の課税区分（qa/03 L-29 で実測したもう 1 つ）。<b>締めまで全文</b>を見る——4 マスタで成り立つ文か。</summary>
    [Fact]
    public async Task 使用中の税区分の課税区分を変えると差し戻される()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var tax = Id(server.TaxCategoryOf("OUT").Value);

        var thrown = await Rejected(server,
            Updating("TaxCategory", Row("TaxCategory", tax, "TaxationType", new SelectFieldData { Value = "taxable_sales" })));

        Assert.Equal(
            "登録できません。この税区分は計上済みの仕訳明細 2 行で使われているので、「課税区分」は変えられません。"
            + "新しい税区分を作って、以後の振替伝票ではそちらを選んでください。",
            thrown.Message);
    }

    /// <summary>部門の「全社共通」と、補助科目の親の付け替え。締めまで全文を見る。</summary>
    [Fact]
    public async Task 使用中の部門と補助科目も意味を変えられない()
    {
        using var server = new AccountingServer();
        // **補助科目を使う科目に付ける**（ADR-0038 §3。1100 現金は使わない科目なので、
        // 補助科目を付けた明細は計上できない——2026-09-08 に塞いだ）。
        var sub = server.InsertSubAccount("1200");
        var dept = server.DepartmentOf("20").Value;
        // 補助科目と部門つきの明細は、フィクスチャの InsertPosted が作れないので SQL で計上する
        // （下書きで書いてから状態を進める。DDL のトリガが唯一許す順序）。
        server.Execute($"""
            insert into journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            values (1, '2026-08-24', '2026-08-24', 'draft', 'normal', '支払', '2026-08-24 10:00:00');
            insert into journal_lines (journal_entry_id, line_no, debit_credit, account_id, sub_account_id, department_id, amount, tax_category_id)
            values ((select max(id) from journal_entries), 1, 'debit', (select id from accounts where code = '1200'), {sub}, {dept}, 500, 1);
            insert into journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
            values ((select max(id) from journal_entries), 2, 'credit', (select id from accounts where code = '2200'), {dept}, 500, 1);
            update journal_entries set status = 'posted', entry_no = 1, posted_at = '2026-08-24 11:00:00'
             where id = (select max(id) from journal_entries);
            """);

        var department = await Rejected(server,
            Updating("Department", Row("Department", Id(dept), "IsCompanyWide", new BooleanFieldData { Value = true })));
        Assert.Equal(
            "登録できません。この部門は計上済みの仕訳明細 2 行で使われているので、「全社共通」は変えられません。"
            + "新しい部門を作って、以後の振替伝票ではそちらを選んでください。",
            department.Message);

        var subAccount = await Rejected(server,
            Updating("SubAccount", Row("SubAccount", Id(sub), "Account", new LinkFieldData { Value = Id(server.AccountOf("2200").Value) })));
        Assert.Equal(
            "登録できません。この補助科目は計上済みの仕訳明細 1 行で使われているので、「勘定科目」は変えられません。"
            + "新しい補助科目を作って、以後の振替伝票ではそちらを選んでください。",
            subAccount.Message);
    }

    /// <summary>
    /// <b>入れ物の名前ではなく、中身の名前で担当を決める</b>（qa/02 R16-16 の型。親子の保存は
    /// 1 つの <c>ModuleSubmitData</c> に混ざって届く——qa/01 F-11）。
    /// <c>ModuleSubmitData.ModuleName</c> で絞る誤実装だと、ここが素通りする。
    /// </summary>
    [Fact]
    public async Task 入れ物の名前が違っても中身の科目は守る()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var cash = Row("Account", Id(server.AccountOf("1100").Value), "Category", new SelectFieldData { Value = "expense" });

        await Rejected(server, Updating("SubAccount", cash));
    }

    /// <summary>
    /// <b>読めない型で届いた欄は「変えた」と見なす</b>——フィールドの型が変わった日に関門ごと消えないため。
    /// 真偽の欄が空で届いた形も同じ（保存されている 0/1 のどちらとも一致しない）。
    /// </summary>
    /// <remarks>
    /// <b>一方通行の列（<c>RequiresPartner</c>）も同じ扱いである</b>——「1 以外はすべて緩めた」と見なす。
    /// ここが素通りすると、<b>その規則だけが静かに消える</b>（自己レビューで指摘された。2026-09-08）。
    /// </remarks>
    [Theory]
    [InlineData("Category", "number", "は変えられません")]
    [InlineData("IsContra", "empty-boolean", "は変えられません")]
    [InlineData("RequiresPartner", "empty-boolean", "をオフにできません")]
    [InlineData("RequiresPartner", "number", "をオフにできません")]
    public async Task 読めない値の欄は変えたと見なして止める(string field, string shape, string expected)
    {
        using var server = new AccountingServer();
        PostPayment(server);
        server.Execute("update accounts set requires_partner = 1 where code = '1100'");
        FieldDataBase value = shape == "number"
            ? new NumberFieldData { Value = 1 }
            : new BooleanFieldData { Value = null };

        var thrown = await Rejected(server,
            Updating("Account", Row("Account", Id(server.AccountOf("1100").Value), field, value)));

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>意味を決める列と一方通行の列を同時に触ったら、意味のほうを断る。</b>
    /// </summary>
    /// <remarks>
    /// マスタの関門は<b>理由を 1 つだけ返す</b>（docs/21 §2-6 の (a)）ので、どちらを見せるかを決めてある——
    /// 意味を決める列は<b>直す手立てが無い</b>（新しい行を作るしかない）が、一方通行のほうは
    /// <b>オンに戻せば通る</b>。<b>重いほうを先に見せる。</b>
    /// </remarks>
    [Fact]
    public async Task 意味を決める列と一方通行の列を同時に変えたら意味のほうを断る()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        server.Execute("update accounts set requires_partner = 1 where code = '1100'");
        var cash = Row("Account", Id(server.AccountOf("1100").Value), "Category", new SelectFieldData { Value = "expense" });
        cash.Fields["RequiresPartner"] = new BooleanFieldData { Value = false };

        var thrown = await Rejected(server, Updating("Account", cash));

        Assert.Contains("「科目区分」は変えられません", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("取引先", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>識別子が読めない更新は止める</b>（値の側と同じく fail-closed）——無い・型が違う。
    /// 更新には必ず数値の識別子が載るので、読めない形は CLB の外から来た壊れた保存である。
    /// 「見ない」で通すと、識別子の型が変わった日に関門ごと消える。
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("text")]
    public async Task 識別子が読めない更新は止める(string shape)
    {
        using var server = new AccountingServer();
        var data = new ModuleData { Name = "Account" };
        data.Fields["Category"] = new SelectFieldData { Value = "expense" };
        if (shape == "text")
        {
            data.Fields["Id"] = new TextFieldData { Value = Id(server.AccountOf("1100").Value) };
        }

        var thrown = await Rejected(server, Updating("Account", data));

        Assert.Equal(
            "登録できません。登録する行を特定できませんでした。画面を開き直してもう一度お試しください。"
            + "同じことが続くときは、管理者にお知らせください。",
            thrown.Message);
    }

    // --- 通す ---------------------------------------------------------------------

    /// <summary>
    /// <b>同じ値に戻した保存は通る</b>（触っただけで変えていない）。
    /// 真偽はオン（1）とオフ（0）の両方を踏む——片方の写像が壊れると、触っていない欄で差し戻される。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 同じ値を送り直す保存は通る(bool contra)
    {
        using var server = new AccountingServer();
        server.Execute($"update accounts set is_contra = {(contra ? 1 : 0)} where code = '1100'");
        PostPayment(server);
        var cash = Row("Account", Id(server.AccountOf("1100").Value), "Category", new SelectFieldData { Value = "asset" });
        cash.Fields["IsContra"] = new BooleanFieldData { Value = contra };

        Assert.True(await Submit(server, Updating("Account", cash)));
    }

    /// <summary>
    /// <b>前後の空白は落として、差分に書き戻す。</b> 画面は空白をそのまま送る（<c>ShouldTrimAfterEdit: false</c>）。
    /// 比べるときだけ落として素通しにすると、関門が「同じ」と通した値を DDL のトリガが「違う」と拒む
    /// （関門の受理集合が DB より広い。qa/03 L-14 の型）。<b>実際に書いて確かめる</b>——書き戻しが無ければ
    /// ここで <c>SqliteException</c> が飛ぶ。
    /// </summary>
    [Fact]
    public async Task 前後の空白は落として差分に書き戻す()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var id = server.AccountOf("1100").Value;
        var code = new TextFieldData { Value = " 1100 " };
        var cash = Row("Account", Id(id), "Code", code);

        await server.Pipeline.SubmitAsync([Updating("Account", cash)], () =>
        {
            server.Execute($"update accounts set code = '{code.Value}' where id = {id}");
            return Task.FromResult(new List<ModuleSubmitResult>());
        });

        Assert.Equal("1100", code.Value);
        Assert.Equal("1100", server.Scalar<string>($"select code from accounts where id = {id}"));
    }

    /// <summary>名前・有効・表示順は意味を決めない（ADR-0038 §2）。</summary>
    [Fact]
    public async Task 名前や有効の変更は使用中でも通る()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var cash = Row("Account", Id(server.AccountOf("1100").Value), "Name", new TextFieldData { Value = "現金及び預金" });
        cash.Fields["IsActive"] = new BooleanFieldData { Value = false };
        cash.Fields["DisplayOrder"] = new NumberFieldData { Value = 9 };

        Assert.True(await Submit(server, Updating("Account", cash)));
    }

    /// <summary>使われていない科目は意味を変えられる。</summary>
    [Fact]
    public async Task 使われていない科目は科目区分を変えられる()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var unused = Id(server.AccountOf("6070").Value);

        Assert.True(await Submit(server,
            Updating("Account", Row("Account", unused, "Category", new SelectFieldData { Value = "asset" }))));
    }

    /// <summary><b>下書きだけが使う科目は変えられる</b>（ADR-0038 §1。違反は計上の関門が拾う）。</summary>
    [Fact]
    public async Task 下書きだけが使う科目は科目区分を変えられる()
    {
        using var server = new AccountingServer();
        server.Execute("""
            insert into journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            values (1, '2026-08-24', '2026-08-24', 'draft', 'normal', '支払', '2026-08-24 10:00:00');
            insert into journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            values (1, 1, 'debit', (select id from accounts where code = '6070'), 500, 1);
            """);

        Assert.True(await Submit(server,
            Updating("Account", Row("Account", Id(server.AccountOf("6070").Value), "Category", new SelectFieldData { Value = "asset" }))));
    }

    /// <summary>新規の行は見ない（仮の識別子。計上済みの明細から参照されえない）。</summary>
    /// <remarks>
    /// <b>追加はコードと名前も伴う。</b> 画面が送ってくる形に揃えてある——
    /// 値の関門（<c>MasterSubmitGate</c>）が<b>追加にコードを要求する</b>ので、
    /// 欠けた検体は意味の凍結に届く前に断られる（2026-09-09）。
    /// </remarks>
    [Fact]
    public async Task 新規の科目は見ない()
    {
        using var server = new AccountingServer();
        PostPayment(server);

        Assert.True(await Submit(server, Adding("Account", Row(
            "Account", "@temporary:1",
            ("Category", new SelectFieldData { Value = "asset" }),
            ("Code", new TextFieldData { Value = "9101" }),
            ("Name", new TextFieldData { Value = "検証" })))));
    }

    /// <summary>更新の側に仮の識別子の行が混ざった形も、新規の行として通す（識別子が読めない更新の唯一の例外）。</summary>
    [Fact]
    public async Task 更新の側に混ざった仮の識別子の行は通す()
    {
        using var server = new AccountingServer();
        PostPayment(server);

        Assert.True(await Submit(server,
            Updating("Account", Row("Account", "@temporary:1", "Category", new SelectFieldData { Value = "expense" }))));
    }

    /// <summary>意味を決める列を触っていない更新は、DB を読まずに通す。</summary>
    [Fact]
    public async Task 意味を決める列を触っていなければ通る()
    {
        using var server = new AccountingServer();
        PostPayment(server);

        Assert.True(await Submit(server,
            Updating("Account", Row("Account", Id(server.AccountOf("1100").Value), "NameKana", new TextFieldData { Value = "げんきん" }))));
    }

    /// <summary>実在しない行の更新は、ここでは見ない（外部キーと楽観ロックが拒む）。</summary>
    [Fact]
    public async Task 実在しない行は見ない()
    {
        using var server = new AccountingServer();

        Assert.True(await Submit(server,
            Updating("Account", Row("Account", "999999", "Category", new SelectFieldData { Value = "asset" }))));
    }

    /// <summary>
    /// <b>空欄で届いた値は、保存されている NULL と同じ「無い」として読む。</b>
    /// 対象外の税区分は税率区分が NULL なので、空のまま送り直しても止めない。逆に、NULL から値を入れる変更は止める。
    /// </summary>
    [Fact]
    public async Task 空欄と保存されている空は同じ値として読む()
    {
        using var server = new AccountingServer();
        PostPayment(server);
        var tax = Id(server.TaxCategoryOf("OUT").Value);
        Assert.Null(server.Scalar<object>($"select rate_kind from tax_categories where id = {tax}"));

        Assert.True(await Submit(server,
            Updating("TaxCategory", Row("TaxCategory", tax, "RateKind", new SelectFieldData { Value = null }))));

        var thrown = await Rejected(server,
            Updating("TaxCategory", Row("TaxCategory", tax, "RateKind", new SelectFieldData { Value = "standard" })));
        Assert.Contains("「税率区分」は変えられません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>関係ないモジュールの更新は素通しする。</summary>
    [Fact]
    public async Task 守っていないモジュールは見ない()
    {
        using var server = new AccountingServer();
        PostPayment(server);

        Assert.True(await Submit(server,
            Updating("CompanyProfile", Row("CompanyProfile", "1", "Name", new TextFieldData { Value = "株式会社れい" }))));
    }

    // --- 写しの突き合わせ（docs/20 §4） -----------------------------------------------

    /// <summary>
    /// 関門が名指しする表・フィールド名・列名・ラベルが、デザイン JSON の
    /// <c>DbTable</c>・<c>Name</c>・<c>DbColumn</c>・<c>DisplayName</c> に一致し、
    /// <b>列の並びが詳細画面の並びと同じ</b>である。
    /// </summary>
    /// <remarks>
    /// ラベルは画面の語の写しなので、片方だけ直ると差し戻しの文言が画面と食い違う。
    /// フィールド名がずれると、その列は<b>黙って守られなくなる</b>（差分に無い名前を探すため）。
    /// 表・列の名前がずれると、比べる相手の値を別の列から読む。
    /// 並びがずれると、差し戻しの文言が画面を上から見直す利用者の目の動きと逆になる。
    /// </remarks>
    [Fact]
    public void 守る表と列の名前とラベルと並びはデザインと一致する()
    {
        foreach (var master in MasterMeaningGate.Guarded)
        {
            using var design = Design(master.ModuleName);
            Assert.Equal(master.Table, design.RootElement.GetProperty("DbTable").GetString());

            var fields = design.RootElement.GetProperty("Fields").EnumerateArray()
                .ToDictionary(f => f.GetProperty("Name").GetString()!);
            // **一方通行の列も、列名とラベルの写しは同じ突き合わせに乗せる。**
            // **並びは見ない**——断りの文言に並べて出すのは意味を決める列だけで、
            // 一方通行の列は 1 列だけを名指しするので、画面の順に依存しない。
            foreach (var column in master.Columns.Concat(master.OneWay.Select(o => o.Column)))
            {
                Assert.True(fields.TryGetValue(column.FieldName, out var field),
                    $"{master.ModuleName}.{column.FieldName} がデザインに無い");
                Assert.Equal(column.Column, field.GetProperty("DbColumn").GetString());
                Assert.Equal(column.Label, field.GetProperty("DisplayName").GetString());
            }

            var guarded = master.Columns.Select(c => c.FieldName).ToHashSet();
            Assert.Equal(
                master.Columns.Select(c => c.FieldName),
                LayoutFieldNames(design.RootElement.GetProperty("DetailLayouts")).Where(guarded.Contains));
        }
    }

    /// <summary>詳細レイアウトに置かれたフィールド名を、置かれている順に列挙する（<c>FieldName</c> を文書順に拾う）。</summary>
    private static IEnumerable<string> LayoutFieldNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "FieldName" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        yield return property.Value.GetString()!;
                    }

                    foreach (var name in LayoutFieldNames(property.Value))
                    {
                        yield return name;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var name in LayoutFieldNames(item))
                    {
                        yield return name;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// <b>関門と DDL のトリガが同じ列を守っている</b>（ADR-0038 §4 の 2 層）。
    /// トリガの <c>UPDATE OF</c> の列と、明細から表を指す列（<c>l.account_id = OLD.id</c>）を定義文から読んで突き合わせる。
    /// </summary>
    /// <remarks>
    /// 片方に列を足して片方に足し忘れると、画面は通すのに DB が拒む（利用者には SQL のエラーが出る）か、
    /// 画面は断るのに取込は通る（守りが 1 層になる）。どちらもテストが緑のままになる型である。
    /// </remarks>
    [Fact]
    public void 関門とトリガは同じ列を守る()
    {
        using var server = new AccountingServer();

        foreach (var master in MasterMeaningGate.Guarded)
        {
            var frozen = TriggerSql(server, $"trg_{master.Table}_meaning_frozen_when_posted");
            var guarded = Regex.Match(frozen, @"UPDATE OF\s+(?<columns>[^\n]+?)\s+ON\s+(?<table>\w+)");
            Assert.True(guarded.Success, $"{master.Table} のトリガに UPDATE OF が無い");
            Assert.Equal(master.Table, guarded.Groups["table"].Value);
            Assert.Equal(
                master.Columns.Select(c => c.Column).Order(),
                guarded.Groups["columns"].Value.Split(',').Select(c => c.Trim()).Order());
            Assert.Contains($"l.{master.LineColumn} = OLD.id", frozen, StringComparison.Ordinal);

            // **伝票にも入る列は、伝票の側も数える**（取引先だけ。docs/10 §6-2）。
            // 明細しか見ないトリガは、伝票にだけ取引先を入れた計上済みの伝票を取りこぼす
            // ——関門は数えるので、画面は断るのに DB は通す（守りが 1 層に落ちる。2026-09-09 の自己レビュー）。
            if (master.EntryColumn is string entryColumn)
            {
                Assert.Contains($"e.{entryColumn} = OLD.id", frozen, StringComparison.Ordinal);
            }

            // **一方通行の列は、緩める向きだけを見る別のトリガが守る**（docs/10 §6-2）。
            // 意味の凍結のトリガに混ぜると、オンにする向きまで止まる。
            foreach (var column in master.OneWay.Select(o => o.Column))
            {
                var oneWay = TriggerSql(server, $"trg_{master.Table}_{column.Column}_not_loosened_when_posted");
                Assert.Contains($"UPDATE OF {column.Column} ON {master.Table}", oneWay, StringComparison.Ordinal);
                Assert.Contains($"OLD.{column.Column} = 1 AND NEW.{column.Column} = 0", oneWay, StringComparison.Ordinal);
                Assert.Contains($"l.{master.LineColumn} = OLD.id", oneWay, StringComparison.Ordinal);
            }

            var replaceInsert = TriggerSql(server, $"trg_{master.Table}_no_replace_used_insert");
            var replaceUpdate = TriggerSql(server, $"trg_{master.Table}_no_replace_used_update");
            Assert.Contains($"l.{master.LineColumn} = NEW.id", replaceInsert, StringComparison.Ordinal);
            Assert.Contains($"l.{master.LineColumn} IN (OLD.id, NEW.id)", replaceUpdate, StringComparison.Ordinal);
            if (master.EntryColumn is string replaceColumn)
            {
                Assert.Contains($"e.{replaceColumn} = NEW.id", replaceInsert, StringComparison.Ordinal);
                Assert.Contains($"e.{replaceColumn} IN (OLD.id, NEW.id)", replaceUpdate, StringComparison.Ordinal);
            }

            // **id が動いたときだけ鳴らす。** `UPDATE OF id` は SET 句に id が並べば
            // 値が同じでも発火するので、この条件が無いと**使用中の行は名前すら直せない**
            // （取引先へ写したときに落ちていた。2026-09-09 の自己レビュー）。
            Assert.Contains("WHEN NEW.id IS NOT OLD.id", replaceUpdate, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>置き換えの守りは、表と列の名前を除いて 4 マスタで 1 字も違わない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>断片一致では、写しから 1 行落ちたことを捕まえられない</b>——
    /// <c>WHEN</c> 節の脱落を実際に見逃した（qa/03 L-37）。
    /// <c>MasterCodeGuardTests.トリガ12本は同じ条件を持つ</c> と同じ形で、<b>全文で突き合わせる</b>。</para>
    /// <para><b>取引先だけは外す。</b> 明細だけでなく伝票の側も数えるので、
    /// EXISTS が 1 つ多い（docs/10 §6-2）。その差は
    /// <see cref="関門とトリガは同じ列を守る"/> と <c>MasterMeaningGuardTests</c> の振る舞いの検体が見る。</para>
    /// </remarks>
    [Theory]
    [InlineData("insert")]
    [InlineData("update")]
    public void 置き換えの守りは4マスタで同じ全文である(string kind)
    {
        using var server = new AccountingServer();

        var normalized = MasterMeaningGate.Guarded
            .Where(m => m.EntryColumn is null)
            .Select(m => Normalize(TriggerSql(server, $"trg_{m.Table}_no_replace_used_{kind}"), m))
            .ToList();

        Assert.Equal(4, normalized.Count);
        Assert.All(normalized, sql => Assert.Equal(normalized[0], sql));
    }

    /// <summary>表・列・トリガ・呼び名を伏せる（残るのは条件の形だけ）。</summary>
    private static string Normalize(string sql, GuardedMaster master)
        => sql.Replace($"l.{master.LineColumn}", "l.@column", StringComparison.Ordinal)
              .Replace($"trg_{master.Table}_", "trg_@table_", StringComparison.Ordinal)
              .Replace($"ON {master.Table}", "ON @table", StringComparison.Ordinal)
              .Replace(master.Label, "@label", StringComparison.Ordinal);

    private static string TriggerSql(AccountingServer server, string name)
        => server.Scalar<string>($"select sql from sqlite_master where type = 'trigger' and name = '{name}'");

    private static JsonDocument Design(string module)
    {
        // フォルダを直書きしない（TestDatabase.QuerySqlOf と同じ理由。部品が増えるたびに動く）
        var path = Directory
            .EnumerateFiles(TestSupport.TestDatabase.ModulesDirectory, $"{module}.mod.json", SearchOption.AllDirectories)
            .Single();
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    // --- 取引先（ADR-0047 の決定 9。2026-09-09 に 4 マスタと揃えた） ----------------

    /// <summary>
    /// 計上済みの明細が使っている取引先は、コードを変えられない。
    /// </summary>
    /// <remarks>
    /// <b>帳簿には取引先の名称と登録番号を焼き込んでいる</b>（ADR-0018）ので、
    /// コードを変えても過去の記載は動かない。それでも揃えるのは、利用者から見て
    /// 「科目コードは変えられないのに取引先コードは変えられる」を説明できないからである。
    /// </remarks>
    [Fact]
    public async Task 計上済みの明細が使っている取引先のコードは変えられない()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner();

        // **計上する前に入れる。** 計上済みの明細は DDL のトリガが変更を拒む（I-05）。
        var entry = server.InsertDraft(
            transactionDate: "2026-08-24", postingDate: "2026-08-24", description: "支払");
        server.InsertLine(entry, 1, "debit", "2200", 1000);
        server.InsertLine(entry, 2, "credit", "1100", 1000);
        server.Execute($"update journal_lines set partner_id = {partner} where journal_entry_id = {entry.Value} and line_no = 1");
        server.Execute(
            $"update journal_entries set status = 'posted', entry_no = 1, posted_at = '2026-08-24 13:00:00' where id = {entry.Value}");

        var thrown = await Rejected(
            server,
            Updating("Partner", Row("Partner", Id(partner), "Code", new TextFieldData { Value = "P999" })));

        Assert.Contains("この取引先は計上済みの振替伝票 1 枚で使われている", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("「取引先コード」は変えられません", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>伝票にだけ入れた取引先も数える</b>（明細が空なら伝票の値が実効値になる。docs/10 §6-2）。
    /// </summary>
    /// <remarks>
    /// <b>明細だけを数えると、この経路を取りこぼす。</b> 取引先は伝票にも明細にも入るので、
    /// 数える単位も「振替伝票の枚数」にしてある——行で数えると同じ伝票を何度も数える。
    /// </remarks>
    [Fact]
    public async Task 伝票にだけ入れた取引先も使用中に数える()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner();
        server.InsertPosted(1, "支払", "2026-08-24", partner, ("debit", "2200", 1000), ("credit", "1100", 1000));

        var thrown = await Rejected(
            server,
            Updating("Partner", Row("Partner", Id(partner), "Code", new TextFieldData { Value = "P999" })));

        Assert.Contains("振替伝票 1 枚", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>下書きだけが使っている取引先は変えられる（使用中は計上済みだけ。ADR-0038 §1）。</summary>
    [Fact]
    public async Task 下書きだけが使う取引先のコードは変えられる()
    {
        using var server = new AccountingServer();
        var partner = server.InsertPartner();
        var draft = server.InsertDraft();
        server.Execute($"update journal_entries set partner_id = {partner} where id = {draft.Value}");

        Assert.True(await Submit(
            server,
            Updating("Partner", Row("Partner", Id(partner), "Code", new TextFieldData { Value = "P999" }))));
    }
}
