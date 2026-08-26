namespace BusinessApp.AccountingCore.Server.Tests.Partners;

using System.Globalization;

using BusinessApp.AccountingCore.Server.Partners;
using BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 取引先を保存するときの関門（docs/07 §1-2）。
/// </summary>
/// <remarks>
/// <b>DDL の CHECK が拒むものを、利用者の言葉で先に止める。</b>
/// ここが外れても DB は守るが、利用者に見えるのは DB の失敗になる。
/// </remarks>
public class PartnerSubmitGateTests
{
    /// <summary>国税庁の計算例（リサーチ §1-1）。検査用数字まで正しい番号。</summary>
    private const string ValidNumber = "8700110005901";

    /// <summary>桁と字種は合っているが検査用数字が違う番号。</summary>
    private const string WrongCheckDigit = "1700110005901";

    private static ModuleSubmitData Adding(params ModuleData[] data)
        => new() { ModuleName = PartnerSubmitGate.ModuleName, Add = [.. data] };

    private static ModuleSubmitData Updating(params ModuleData[] data)
        => new() { ModuleName = PartnerSubmitGate.ModuleName, Update = [.. data] };

    /// <summary>CLB は<b>変更されたフィールドしか送ってこない</b>ので、渡された項目だけ載せる。</summary>
    private static ModuleData Partner(
        long? id = null, string? corporateNumber = null, string? entityType = null, string? parentId = null)
    {
        var data = new ModuleData { Name = PartnerSubmitGate.ModuleName };

        if (id is long rowId)
        {
            data.Fields["Id"] = new IdFieldData { Value = rowId.ToString(CultureInfo.InvariantCulture) };
        }

        if (corporateNumber is not null)
        {
            data.Fields["CorporateNumber"] = new TextFieldData { Value = corporateNumber };
        }

        if (entityType is not null)
        {
            data.Fields["EntityType"] = new SelectFieldData { Value = entityType };
        }

        if (parentId is not null)
        {
            // 参照フィールド（LinkFieldDesign）のデータは LinkFieldData で、識別子は Value に入る。
            data.Fields["ParentPartner"] = new LinkFieldData { Value = parentId };
        }

        return data;
    }

    private static PartnerSubmitGate Gate(AccountingServer server) => new(server.Partners);

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

    private static async Task<PartnerRejectedException> RejectedAsync(
        AccountingServer server, ModuleSubmitData submitted, SaveSpy save)
    {
        var rejected = await Assert.ThrowsAsync<PartnerRejectedException>(
            () => Gate(server).SubmitAsync([submitted], save.SaveAsync));

        Assert.False(save.Called);

        // トースト内の文字列は改行できない（qa/01 D-12）。全部の文言をここで見る。
        Assert.DoesNotContain("\n", rejected.Message, StringComparison.Ordinal);
        return rejected;
    }

    [Fact]
    public async Task 正しい法人番号は通す()
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(Partner(corporateNumber: ValidNumber))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>法人番号は任意（docs/07 §2-3）。空欄で止めない。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空欄の法人番号は通す(string value)
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(Partner(corporateNumber: value))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>差分に法人番号が無い保存（名称だけ直した等）は、番号を検査しない。</summary>
    [Fact]
    public async Task 法人番号を触っていない保存は通す()
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Updating(Partner(id: 1))], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Theory]
    [InlineData("123456789012")]
    [InlineData("12345678901234")]
    [InlineData("87001100059O1")]
    public async Task 桁や字種が違う法人番号を弾く(string value)
    {
        using var server = new AccountingServer();

        var rejected = await RejectedAsync(server, Adding(Partner(corporateNumber: value)), new SaveSpy());

        Assert.Contains("13 桁", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>桁が合っていても検査用数字が違えば止める。</b>
    /// 通すと、打ち間違えた番号がそのまま名寄せの自然キーになる（docs/07 §2-2）。
    /// </summary>
    [Fact]
    public async Task 検査用数字が合わない法人番号を弾く()
    {
        using var server = new AccountingServer();

        var rejected = await RejectedAsync(server, Adding(Partner(corporateNumber: WrongCheckDigit)), new SaveSpy());

        // 書式の説明ではなく、打ち間違いとして知らせる。
        Assert.DoesNotContain("13 桁", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("法人番号公表サイト", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>貼り付けの空白を落として保存する。2 通りの文字列で保存させない。</summary>
    [Fact]
    public async Task 前後の空白を落として保存する()
    {
        using var server = new AccountingServer();
        var data = Partner(corporateNumber: $"  {ValidNumber} ");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(data)], save.SaveAsync);

        Assert.True(save.Called);
        Assert.Equal(ValidNumber, (data.Fields["CorporateNumber"] as TextFieldData)?.Value);
    }

    [Fact]
    public async Task 個人事業者に法人番号は入れられない()
    {
        using var server = new AccountingServer();

        var rejected = await RejectedAsync(
            server,
            Adding(Partner(corporateNumber: ValidNumber, entityType: "sole_proprietor")),
            new SaveSpy());

        // 列挙子の英語名を文言に混ぜない（CLAUDE.md §2-7）。
        Assert.Contains("個人事業者", rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sole_proprietor", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>種別が差分に無くても止める。</b> 保存されている種別を読みに行かないと、
    /// 「先に個人事業者にしておいて、あとから法人番号だけ足す」で素通りする。
    /// </summary>
    [Fact]
    public async Task 保存されている種別が個人事業者なら法人番号だけの更新も弾く()
    {
        using var server = new AccountingServer();
        var id = InsertPartner(server, entityType: "sole_proprietor");

        await RejectedAsync(server, Updating(Partner(id: id, corporateNumber: ValidNumber)), new SaveSpy());
    }

    /// <summary>逆向き。番号が入っている取引先を、あとから個人事業者に変える更新。</summary>
    [Fact]
    public async Task 保存されている法人番号があるなら種別だけの更新も弾く()
    {
        using var server = new AccountingServer();
        var id = InsertPartner(server, corporateNumber: ValidNumber);

        await RejectedAsync(server, Updating(Partner(id: id, entityType: "sole_proprietor")), new SaveSpy());
    }

    /// <summary>
    /// <b>個人事業者そのものは通る。</b> 拒む側だけを検査すると、
    /// 「個人事業者を一律に拒む」実装になっても全件緑になる（<c>&gt; 0</c> を <c>&gt;= 0</c> にする変異）。
    /// </summary>
    [Fact]
    public async Task 法人番号のない個人事業者は通す()
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(Partner(entityType: "sole_proprietor"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>個人事業者に直すために法人番号を消す</b>——docs/07 §1-2 が想定している正規の直し方。
    /// ここが通らないと、矛盾した行を直す手段が無くなる。
    /// </summary>
    [Fact]
    public async Task 保存されている法人番号を消してから個人事業者にできる()
    {
        using var server = new AccountingServer();
        var id = InsertPartner(server, corporateNumber: ValidNumber);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: id, entityType: "sole_proprietor", corporateNumber: ""))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>差分の値が保存されている値に勝つ。</b> 個人事業者だった取引先を法人に直しつつ
    /// 法人番号を入れる保存は通る。保存値を優先する実装だと、ここで差し戻してしまう。
    /// </summary>
    [Fact]
    public async Task 個人事業者を法人に直しながら法人番号を入れられる()
    {
        using var server = new AccountingServer();
        var id = InsertPartner(server, entityType: "sole_proprietor");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: id, entityType: "corporation", corporateNumber: ValidNumber))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>空欄は空文字ではなく NULL で渡す。</b>
    /// </summary>
    /// <remarks>
    /// DDL の CHECK は「NULL か、数字 13 桁」しか許さない（<c>004_masters.sql</c>）ので、
    /// 空文字を書き戻すと<b>関門自身が DB の失敗を作る</b>。
    /// 下の <c>関門が通した値は_DB_も受け取れる</c> が、それを DB に流して確かめる。
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空欄の法人番号は_null_にして渡す(string value)
    {
        using var server = new AccountingServer();
        var data = Partner(corporateNumber: value);

        await Gate(server).SubmitAsync([Adding(data)], new SaveSpy().SaveAsync);

        Assert.Null((data.Fields["CorporateNumber"] as TextFieldData)?.Value);
    }

    /// <summary>
    /// <b>関門が通した値を、そのまま DB が受け取れる。</b>
    /// </summary>
    /// <remarks>
    /// 関門の役目は「DDL の CHECK の手前に置く網」（docs/07 §1-5）なので、
    /// <b>関門の受理集合が DB の受理集合に収まっていなければ意味を成さない</b>。
    /// 保存が呼ばれたかどうかだけを見ていると、関門が書き換えた値が DB に拒まれることに気づけない
    /// （実際に空文字で作り込んだ。qa/03 L-14）。
    /// </remarks>
    [Theory]
    [InlineData(ValidNumber, "corporation")]
    [InlineData("", "sole_proprietor")]
    [InlineData("   ", null)]
    public async Task 関門が通した値は_DB_も受け取れる(string corporateNumber, string? entityType)
    {
        using var server = new AccountingServer();
        var data = Partner(corporateNumber: corporateNumber, entityType: entityType);

        await Gate(server).SubmitAsync([Adding(data)], new SaveSpy().SaveAsync);

        var number = (data.Fields["CorporateNumber"] as TextFieldData)?.Value;
        var type = data.Fields.TryGetValue("EntityType", out var field)
            ? (field as SelectFieldData)?.Value
            : null;
        server.Execute($"""
            insert into partners (code, name, corporate_number, entity_type)
            values ('P950', '関門を通った取引先', {Literal(number)}, {Literal(type)})
            """);

        Assert.Equal(1L, server.Scalar<long>("select count(*) from partners where code = 'P950'"));
    }

    /// <summary>SQL のリテラル。<c>null</c> は引用符で包まずに <c>null</c> と書く。</summary>
    private static string Literal(string? value) => value is null ? "null" : $"'{value}'";

    /// <summary>
    /// <b>同じ保存に他のモジュールが混ざっていても効く。</b> 伝票を入力しながら取引先を直す形。
    /// </summary>
    [Fact]
    public async Task 仕訳と混ざった保存でも取引先の関門は効く()
    {
        using var server = new AccountingServer();

        await RejectedAsync(
            server,
            new ModuleSubmitData
            {
                ModuleName = "JournalEntry",
                Add = [SubmitData.Entry("1"), Partner(corporateNumber: WrongCheckDigit)],
            },
            new SaveSpy());
    }

    /// <summary>
    /// <b>1 回の保存に取引先が 2 件</b>。1 件目だけを見る実装だと、2 件目の壊れた番号が通る。
    /// </summary>
    [Fact]
    public async Task 同じ保存の二件目の取引先も見る()
    {
        using var server = new AccountingServer();

        await RejectedAsync(
            server,
            Adding(Partner(corporateNumber: ValidNumber), Partner(corporateNumber: WrongCheckDigit)),
            new SaveSpy());
    }

    /// <summary>法人は法人番号を持てる（拒む側だけを検査すると、一律に拒む実装でも緑になる）。</summary>
    [Fact]
    public async Task 法人には法人番号を入れられる()
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Partner(corporateNumber: ValidNumber, entityType: "corporation"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>人格のない社団等も法人番号を持ちうる（docs/07 §1-2）。</summary>
    [Fact]
    public async Task 人格のない社団等には法人番号を入れられる()
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Partner(corporateNumber: ValidNumber, entityType: "unincorporated_association"))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// 読めない種別は「未分類」として扱い、ここでは止めない。
    /// <b>拒むのは DDL の CHECK の仕事</b>で、ここで例外にすると 500 になる。
    /// </summary>
    [Theory]
    [InlineData("company")]
    [InlineData("0")]
    [InlineData("99")]
    public async Task 読めない種別は未分類として扱う(string entityType)
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Partner(corporateNumber: ValidNumber, entityType: entityType))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>保存されていない取引先を指す更新（同時に消された等）でも、関門自身は落ちない。</summary>
    [Fact]
    public async Task 実在しない取引先の更新でも落ちない()
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Updating(Partner(id: 999, entityType: "corporation"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Fact]
    public async Task 自分自身を名寄せの親にできない()
    {
        using var server = new AccountingServer();
        var id = InsertPartner(server);

        var rejected = await RejectedAsync(
            server,
            Updating(Partner(id: id, parentId: id.ToString(CultureInfo.InvariantCulture))),
            new SaveSpy());

        Assert.Contains("名寄せの親", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 別の取引先は名寄せの親にできる()
    {
        using var server = new AccountingServer();
        var parent = InsertPartner(server, code: "P901");
        var child = InsertPartner(server, code: "P902");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: child, parentId: parent.ToString(CultureInfo.InvariantCulture)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// 新規作成の行は識別子を持たない（仮の値が入る）ので、自己参照になりようがない。
    /// <b>仮の識別子を数値として読もうとして落ちない</b>ことも一緒に見る。
    /// </summary>
    [Fact]
    public async Task 新規作成の親指定は通す()
    {
        using var server = new AccountingServer();
        var parent = InsertPartner(server);
        var data = Partner(parentId: parent.ToString(CultureInfo.InvariantCulture));
        data.Fields["Id"] = new IdFieldData { Value = "@temporary:0f0a" };
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(data)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>名寄せの親が空欄なら何も見ない。</summary>
    [Fact]
    public async Task 名寄せの親が空欄でも通す()
    {
        using var server = new AccountingServer();
        var id = InsertPartner(server);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Updating(Partner(id: id, parentId: ""))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>他のモジュールの保存には手を出さない。</summary>
    [Fact]
    public async Task 他のモジュールの保存は素通しする()
    {
        using var server = new AccountingServer();
        // **入れ物の ModuleName ではなく、中身の ModuleData.Name で見分ける**（qa/01 F-11）。
        // 入れ物を取引先にしておくので、名前で絞っていない実装はここで差し戻してしまう。
        var other = new ModuleData { Name = "Account" };
        other.Fields["CorporateNumber"] = new TextFieldData { Value = "こわれた番号" };
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [new ModuleSubmitData { ModuleName = PartnerSubmitGate.ModuleName, Add = [other] }], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Fact]
    public async Task 保存を渡さなければ止まる()
    {
        using var server = new AccountingServer();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Gate(server).SubmitAsync([], null!));
    }

    [Fact]
    public async Task 保存の中身を渡さなければ止まる()
    {
        using var server = new AccountingServer();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => Gate(server).SubmitAsync(null!, new SaveSpy().SaveAsync));
    }

    private static long InsertPartner(
        AccountingServer server,
        string code = "P900",
        string? entityType = null,
        string? corporateNumber = null)
    {
        var type = entityType is null ? "null" : $"'{entityType}'";
        var number = corporateNumber is null ? "null" : $"'{corporateNumber}'";

        server.Execute($"""
            insert into partners (code, name, is_active, entity_type, corporate_number)
            values ('{code}', '取引先 {code}', 1, {type}, {number})
            """);

        return server.Scalar<long>("select last_insert_rowid()");
    }
}
