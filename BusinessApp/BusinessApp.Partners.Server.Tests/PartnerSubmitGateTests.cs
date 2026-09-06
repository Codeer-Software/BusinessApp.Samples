namespace BusinessApp.Partners.Server.Tests;

using System.Globalization;

using BusinessApp.Partners.Server;
using BusinessApp.Partners.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

using Microsoft.Data.Sqlite;

/// <summary>
/// 取引先を保存するときの関門（docs/13 §1-2）。
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

    private static PartnerSubmitGate Gate(PartnerServer server) => new(server.Partners);

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
        PartnerServer server, ModuleSubmitData submitted, SaveSpy save)
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
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(Partner(corporateNumber: ValidNumber))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>法人番号は任意（docs/13 §2-3）。空欄で止めない。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 空欄の法人番号は通す(string value)
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(Partner(corporateNumber: value))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>差分に法人番号が無い保存（名称だけ直した等）は、番号を検査しない。</summary>
    [Fact]
    public async Task 法人番号を触っていない保存は通す()
    {
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();

        var rejected = await RejectedAsync(server, Adding(Partner(corporateNumber: value)), new SaveSpy());

        Assert.Contains("13 桁", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>桁が合っていても検査用数字が違えば止める。</b>
    /// 通すと、打ち間違えた番号がそのまま名寄せの自然キーになる（docs/13 §2-2）。
    /// </summary>
    [Fact]
    public async Task 検査用数字が合わない法人番号を弾く()
    {
        using var server = new PartnerServer();

        var rejected = await RejectedAsync(server, Adding(Partner(corporateNumber: WrongCheckDigit)), new SaveSpy());

        // 書式の説明ではなく、打ち間違いとして知らせる。
        Assert.DoesNotContain("13 桁", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("法人番号公表サイト", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>貼り付けの空白を落として保存する。2 通りの文字列で保存させない。</summary>
    [Fact]
    public async Task 前後の空白を落として保存する()
    {
        using var server = new PartnerServer();
        var data = Partner(corporateNumber: $"  {ValidNumber} ");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(data)], save.SaveAsync);

        Assert.True(save.Called);
        Assert.Equal(ValidNumber, (data.Fields["CorporateNumber"] as TextFieldData)?.Value);
    }

    [Fact]
    public async Task 個人事業者に法人番号は入れられない()
    {
        using var server = new PartnerServer();

        var rejected = await RejectedAsync(
            server,
            Adding(Partner(corporateNumber: ValidNumber, entityType: "sole_proprietor")),
            new SaveSpy());

        // 列挙子の英語名を文言に混ぜない（docs/21_画面の原則.md §2）。
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
        using var server = new PartnerServer();
        var id = InsertPartner(server, entityType: "sole_proprietor");

        await RejectedAsync(server, Updating(Partner(id: id, corporateNumber: ValidNumber)), new SaveSpy());
    }

    /// <summary>逆向き。番号が入っている取引先を、あとから個人事業者に変える更新。</summary>
    [Fact]
    public async Task 保存されている法人番号があるなら種別だけの更新も弾く()
    {
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Adding(Partner(entityType: "sole_proprietor"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>個人事業者に直すために法人番号を消す</b>——docs/13 §1-2 が想定している正規の直し方。
    /// ここが通らないと、矛盾した行を直す手段が無くなる。
    /// </summary>
    [Fact]
    public async Task 保存されている法人番号を消してから個人事業者にできる()
    {
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
        var data = Partner(corporateNumber: value);

        await Gate(server).SubmitAsync([Adding(data)], new SaveSpy().SaveAsync);

        Assert.Null((data.Fields["CorporateNumber"] as TextFieldData)?.Value);
    }

    /// <summary>
    /// <b>関門が通した値を、そのまま DB が受け取れる。</b>
    /// </summary>
    /// <remarks>
    /// 関門の役目は「DDL の CHECK の手前に置く網」（docs/13 §1-5）なので、
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
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();

        await RejectedAsync(
            server,
            new ModuleSubmitData
            {
                ModuleName = "JournalEntry",
                Add = [ForeignModuleData.Entry("1"), Partner(corporateNumber: WrongCheckDigit)],
            },
            new SaveSpy());
    }

    /// <summary>
    /// <b>1 回の保存に取引先が 2 件</b>。1 件目だけを見る実装だと、2 件目の壊れた番号が通る。
    /// </summary>
    [Fact]
    public async Task 同じ保存の二件目の取引先も見る()
    {
        using var server = new PartnerServer();

        await RejectedAsync(
            server,
            Adding(Partner(corporateNumber: ValidNumber), Partner(corporateNumber: WrongCheckDigit)),
            new SaveSpy());
    }

    /// <summary>法人は法人番号を持てる（拒む側だけを検査すると、一律に拒む実装でも緑になる）。</summary>
    [Fact]
    public async Task 法人には法人番号を入れられる()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Partner(corporateNumber: ValidNumber, entityType: "corporation"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>人格のない社団等も法人番号を持ちうる（docs/13 §1-2）。</summary>
    [Fact]
    public async Task 人格のない社団等には法人番号を入れられる()
    {
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Partner(corporateNumber: ValidNumber, entityType: entityType))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>保存されていない取引先を指す更新（同時に消された等）でも、関門自身は落ちない。</summary>
    [Fact]
    public async Task 実在しない取引先の更新でも落ちない()
    {
        using var server = new PartnerServer();
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Updating(Partner(id: 999, entityType: "corporation"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    [Fact]
    public async Task 自分自身を名寄せの親にできない()
    {
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();
        var id = InsertPartner(server);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync([Updating(Partner(id: id, parentId: ""))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>他のモジュールの保存には手を出さない。</summary>
    [Fact]
    public async Task 他のモジュールの保存は素通しする()
    {
        using var server = new PartnerServer();
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
        using var server = new PartnerServer();

        await Assert.ThrowsAsync<ArgumentNullException>(() => Gate(server).SubmitAsync([], null!));
    }

    /// <summary>
    /// 保存の中身を渡さなければ止まる。<b>引数名まで表明する。</b>
    /// </summary>
    /// <remarks>
    /// 型だけを見ると、ガードを消しても中の LINQ が同じ
    /// <see cref="ArgumentNullException"/>（<c>ParamName</c> は <c>"source"</c>）を投げるので
    /// <b>テストは通ったまま</b>になる（2026-08-27 の自己レビュー R16-04）。
    /// </remarks>
    [Fact]
    public async Task 保存の中身を渡さなければ止まる()
    {
        using var server = new PartnerServer();

        var rejected = await Assert.ThrowsAsync<ArgumentNullException>(
            () => Gate(server).SubmitAsync(null!, new SaveSpy().SaveAsync));

        Assert.Equal("transactionData", rejected.ParamName);
    }

    // --- 名寄せの親（ADR-0028。深さ 1 の森と、種別の食い違い）---

    /// <summary>
    /// <b>個人事業者と法人系は互いに親にできない。</b>
    /// </summary>
    /// <remarks>
    /// 止めることで生まれるのは「束ね漏れ」の側（控除が過大になる方向）だが、
    /// <b>種別が食い違う組は、束ねるべきなら種別のどちらかが誤っている</b>。
    /// 関門は「束ねるな」ではなく「先に種別を直せ」と言っている（ADR-0028 の理由節）。
    /// </remarks>
    [Theory]
    [InlineData("sole_proprietor", "corporation")]
    [InlineData("sole_proprietor", "unincorporated_association")]
    [InlineData("corporation", "sole_proprietor")]
    [InlineData("unincorporated_association", "sole_proprietor")]
    public async Task 個人事業者と法人系は互いに親にできない(string childType, string parentType)
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800", entityType: parentType);

        var rejected = await RejectedAsync(
            server,
            Adding(Partner(entityType: childType, parentId: parent.ToString(CultureInfo.InvariantCulture))),
            new SaveSpy());

        // **文言まで固定する。** 「同じ事業者なら、どちらかの種別が誤っています」は
        // 関門が「束ねるな」ではなく「先に種別を直せ」と言っている、という設計そのものである。
        Assert.Equal(
            $"{PartnerRejectedException.Headline}。"
            + "個人事業者と法人・人格のない社団等は、互いに名寄せの親にできません。"
            + "同じ事業者なら、どちらかの種別が誤っています。",
            rejected.Message);
    }

    /// <summary>
    /// <b>それ以外の種別違いは通す。</b>
    /// </summary>
    /// <remarks>
    /// <para>公表システムの人格区分は「1 個人／2 法人（人格のない社団等を含む）」の 2 値で、
    /// 本プロジェクトの 4 値より粗い。<b>法人と人格のない社団等を止めると、
    /// 取込（フェーズ 6）で入った行どうしが機械的に弾かれる</b>。</para>
    /// <para>未分類（NULL）と「その他」は人格を何も表していないので通す。</para>
    /// </remarks>
    [Theory]
    [InlineData("corporation", "unincorporated_association")]
    [InlineData("unincorporated_association", "corporation")]
    [InlineData("other", "sole_proprietor")]
    [InlineData("sole_proprietor", "other")]
    [InlineData(null, "sole_proprietor")]
    [InlineData("sole_proprietor", null)]
    public async Task 個人事業者と法人系の組でなければ通す(string? childType, string? parentType)
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800", entityType: parentType);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Adding(Partner(entityType: childType, parentId: parent.ToString(CultureInfo.InvariantCulture)))],
            save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>種別だけを直した保存でも、保存済みの親と突き合わせる。</b>
    /// </summary>
    /// <remarks>
    /// 差分に親が無いからと諦めると、「先に親を付けておいて、あとから種別を食い違わせる」で素通りする。
    /// </remarks>
    [Fact]
    public async Task 種別だけの更新でも保存済みの親と突き合わせる()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800", entityType: "corporation");
        var child = InsertPartner(server, "P801", entityType: "corporation", parentId: parent);

        var rejected = await RejectedAsync(
            server, Updating(Partner(id: child, entityType: "sole_proprietor")), new SaveSpy());

        Assert.Contains("互いに名寄せの親にできません", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>子を持つ取引先の種別を変えて、食い違わせられない。</b>
    /// </summary>
    /// <remarks>
    /// <para>子から親を見るだけだと、この方向が素通りする。ADR-0028 の帰結が
    /// 「<b>親と子のどちらを直す場合も検査が要る</b>。『親の種別を変えて食い違わせる』の
    /// 3 方向すべてを自動テストと実機の台本に入れる」と名指ししていた方向である
    /// （2026-08-31 の自己レビューで、実装されていないことが分かった）。</para>
    /// <para><b>DDL にも種別の規則は無い</b>（トリガが見るのは深さだけ）ので、
    /// ここが素通りすると最後の砦も無い。</para>
    /// </remarks>
    [Fact]
    public async Task 子を持つ取引先の種別を食い違わせられない()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800", entityType: "corporation");
        InsertPartner(server, "P801", entityType: "corporation", parentId: parent);

        var rejected = await RejectedAsync(
            server, Updating(Partner(id: parent, entityType: "sole_proprietor")), new SaveSpy());

        Assert.Contains("互いに名寄せの親にできません", rejected.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>子が居ても、食い違わない種別なら変えられる。</b>
    /// </summary>
    /// <remarks>拒む側だけを見ると、「子が居たら種別を変えられない」実装でも緑になる。</remarks>
    [Fact]
    public async Task 子を持つ取引先でも食い違わない種別には変えられる()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800", entityType: "corporation");
        InsertPartner(server, "P801", entityType: "corporation", parentId: parent);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: parent, entityType: "unincorporated_association"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>種別を触っていない保存では、子との突き合わせをしない。</summary>
    [Fact]
    public async Task 種別を触っていない保存は子と突き合わせない()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800", entityType: "corporation");
        InsertPartner(server, "P801", entityType: "sole_proprietor", parentId: parent);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: parent, corporateNumber: ValidNumber))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>親が、さらに親を持っていてはいけない（深さ 1 の森。ADR-0028 §2）。</summary>
    [Fact]
    public async Task 親を持つ取引先は親にできない()
    {
        using var server = new PartnerServer();
        var root = InsertPartner(server, "P800");
        var middle = InsertPartner(server, "P801", parentId: root);

        var rejected = await RejectedAsync(
            server,
            Adding(Partner(parentId: middle.ToString(CultureInfo.InvariantCulture))),
            new SaveSpy());

        Assert.Equal(
            $"{PartnerRejectedException.Headline}。"
            + "名寄せの親には、さらに親を持つ取引先を選べません。"
            + "同じ事業者なら、その取引先の親を選んでください。",
            rejected.Message);
    }

    /// <summary>
    /// <b>親 FK が識別子フィールドで来ても、深さを見る。</b>
    /// </summary>
    /// <remarks>
    /// 型を 1 つに決め打ちすると、フィールドの型が変わった日に
    /// <b>自己親・種別の食い違い・深さ 1 の 3 本がまとめて素通しに落ちる</b>
    /// ——しかもフィクスチャが自分で <c>LinkFieldData</c> を組むのでテストは緑のまま
    /// （2026-08-31 の自己レビュー。登録の関門で同じ穴を同じ日に直したのに、こちらに残っていた）。
    /// </remarks>
    [Fact]
    public async Task 親FKが識別子フィールドでも深さを見る()
    {
        using var server = new PartnerServer();
        var root = InsertPartner(server, "P800");
        var middle = InsertPartner(server, "P801", parentId: root);

        var row = Partner();
        row.Fields["ParentPartner"] = new IdFieldData
        {
            Value = middle.ToString(CultureInfo.InvariantCulture),
        };

        await RejectedAsync(server, Adding(row), new SaveSpy());
    }

    /// <summary>想定していない型で親が来たら、突き合わせの対象にしない。</summary>
    /// <remarks>
    /// CLB は宣言した型でしか送らないので、ここに来るのは API を直に叩いた経路だけである。
    /// 例外にせず素通しするのは、外部キーが最後に受け止めるからである。
    /// </remarks>
    [Fact]
    public async Task 想定していない型の親は突き合わせに使わない()
    {
        using var server = new PartnerServer();
        var root = InsertPartner(server, "P800");
        InsertPartner(server, "P801", parentId: root);
        var save = new SaveSpy();

        var row = Partner();
        row.Fields["ParentPartner"] = new NumberFieldData { Value = root };

        await Gate(server).SubmitAsync([Adding(row)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>自分が誰かの親になっているなら、自分に親は付けられない（同上）。</summary>
    [Fact]
    public async Task 誰かの親になっている取引先には親を付けられない()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800");
        InsertPartner(server, "P801", parentId: parent);
        var root = InsertPartner(server, "P802");

        var rejected = await RejectedAsync(
            server,
            Updating(Partner(id: parent, parentId: root.ToString(CultureInfo.InvariantCulture))),
            new SaveSpy());

        Assert.Equal(
            $"{PartnerRejectedException.Headline}。"
            + "この取引先は他の取引先の名寄せの親になっているので、親を付けられません。"
            + "先に、子になっている取引先の親を付け替えてください。",
            rejected.Message);
    }

    /// <summary>
    /// <b>根どうしなら付けられる。</b>
    /// </summary>
    /// <remarks>
    /// <para>拒む側だけを検査すると、「親の指定を一律に拒む」実装でも全件緑になる。</para>
    /// <para><b>検体を縮退させない</b>（2026-08-31 の自己レビュー R28-15）。
    /// 2 件とも親も子も持たない根だと、<b>「親を持つか」と「子を持つか」のどちらを見ていても同じ結果</b>に
    /// なる。親のほうに<b>別の子</b>を付けてあるので、<b>「親になっている取引先は親を持てない」を
    /// 「親になっている取引先は親になれない」と読み違えた実装</b>だけが、ここで落ちる。</para>
    /// <para><b>DB も受け取ることまで見る。</b> 関門が通しただけでは、トリガが同じ保存を拒む
    /// （＝関門と DB の規則がずれている）ことに気づけない。</para>
    /// </remarks>
    [Fact]
    public async Task 根どうしなら親を付けられる()
    {
        using var server = new PartnerServer();
        var root = InsertPartner(server, "P800");
        InsertPartner(server, "P802", parentId: root);    // 親のほうは、既に別の子を持っている
        var child = InsertPartner(server, "P801");
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: child, parentId: root.ToString(CultureInfo.InvariantCulture)))],
            save.SaveAsync);

        Assert.True(save.Called);

        // 関門が通した保存を、DDL のトリガも受け取る。
        server.Execute($"update partners set parent_partner_id = {root} where id = {child}");
        Assert.Equal(root, server.Scalar<long>($"select parent_partner_id from partners where id = {child}"));
    }

    /// <summary>
    /// <b>親を空欄に戻す保存は、いつでも通る。</b>
    /// </summary>
    /// <remarks>
    /// 子を持つ取引先でも、自分の親を外すのは深さを浅くする操作である。
    /// ここを止めると、矛盾した行を直す唯一の手を塞ぐことになる。
    /// </remarks>
    [Fact]
    public async Task 親を空欄に戻す保存は通す()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800");
        InsertPartner(server, "P801", parentId: parent);
        var save = new SaveSpy();

        var cleared = Partner(id: parent);
        cleared.Fields["ParentPartner"] = new LinkFieldData { Value = string.Empty };

        await Gate(server).SubmitAsync([Updating(cleared)], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>子を持つ取引先でも、他の項目だけを直す保存は通る。</b>
    /// </summary>
    /// <remarks>
    /// 深さは<b>親を付け替えたときにしか変わらない</b>。触っていない行まで見ると、
    /// 「誰かの親になっているから、この取引先はもう何も直せない」という画面ができる。
    /// docs/13 §1-2 の「触っていない行に分類を強制しない」と同じ考え方である。
    /// </remarks>
    [Fact]
    public async Task 子を持つ取引先でも他の項目は直せる()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800");
        InsertPartner(server, "P801", parentId: parent);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: parent, corporateNumber: ValidNumber))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>種別も親も触っていない保存は、食い違いを見ない。</b>
    /// </summary>
    /// <remarks>
    /// <para>見ると、<b>関門より前に入った食い違いの行を、他の項目を直すだけでも保存できなくする</b>。
    /// docs/13 §1-2 の「触っていない行に分類を強制しない」と同じ考え方である
    /// （2026-08-31 の自己レビューで、早期 return を消す変異が生き残って気づいた）。</para>
    /// <para>検体は SQL で直接作る。<b>関門を迂回しているのではなく</b>、
    /// 関門も DDL のトリガも種別の組までは見ていなかった時代のデータを作っている
    /// （トリガが見るのは深さだけである）。</para>
    /// </remarks>
    [Fact]
    public async Task 種別も親も触っていない保存は食い違いを見ない()
    {
        using var server = new PartnerServer();
        var parent = InsertPartner(server, "P800", entityType: "corporation");
        var child = InsertPartner(server, "P801", entityType: "sole_proprietor", parentId: parent);
        var save = new SaveSpy();

        await Gate(server).SubmitAsync(
            [Updating(Partner(id: child, corporateNumber: string.Empty))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>読めない相手を指した保存は、深さを見ない。</b>
    /// </summary>
    /// <remarks>
    /// <para>居ない取引先を親に指した保存（API 直叩き）と、保存されていない行の更新。
    /// どちらも深さを判定する材料が無いので、ここでは止めない——外部キーが最後に拒む。
    /// <b>見つからないことを「深さ 0」と読むと、逆に素通しの穴になる</b>ので、経路を固定する。</para>
    /// <para><b>「外部キーが最後に拒む」を実際に流す</b>（2026-08-31 の自己レビュー R28-15）。
    /// <c>SaveSpy</c> は DB に書かないので、<b>この主張は書いてあるだけで一度も走っていなかった</b>——
    /// 外部キーの宣言を落としても、あるいは <c>PRAGMA foreign_keys</c> が効いていなくても緑になる。</para>
    /// </remarks>
    [Fact]
    public async Task 読めない相手を指した保存は深さを見ない()
    {
        using var server = new PartnerServer();
        var root = InsertPartner(server, "P800");
        var save = new SaveSpy();

        // 居ない取引先を親に指す。
        await Gate(server).SubmitAsync([Adding(Partner(parentId: "9990"))], save.SaveAsync);
        // 保存されていない行に、実在する親を指す。
        await Gate(server).SubmitAsync(
            [Updating(Partner(id: 9991, parentId: root.ToString(CultureInfo.InvariantCulture)))],
            save.SaveAsync);

        Assert.True(save.Called);

        // **最後に拒むのは外部キーである。** 関門が通したあと、DB がこの行を受け取らない。
        var thrown = Assert.Throws<SqliteException>(() => InsertPartner(server, "P801", parentId: 9990));
        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT, thrown.SqliteErrorCode);
        Assert.Contains("FOREIGN KEY", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>DDL のトリガも同じ規則を持っている。</b>
    /// </summary>
    /// <remarks>
    /// <para>関門は手前の網で、最後に守るのは DB である（ADR-0004 と同じ形）。
    /// <b>取込・API・SQL の直打ちは関門を通らない。</b></para>
    /// <para><b>どの規則に当たったかまで見る</b>（2026-08-31 の自己レビュー R28-15）。
    /// 例外の型だけを見ていると、<b>外部キー違反でも一意制約でも同じく緑</b>になる——
    /// トリガを消しても、検体がたまたま別の制約に当たれば気づけない。
    /// トリガは <c>RAISE(ABORT, …)</c> でメッセージを持つので、そこで見分ける。</para>
    /// </remarks>
    [Fact]
    public void 深さ二の親子はDBも拒む()
    {
        using var server = new PartnerServer();
        var root = InsertPartner(server, "P800");
        var middle = InsertPartner(server, "P801", parentId: root);

        // ①親を持つ取引先を、さらに誰かの親にする（INSERT のトリガ）。
        var adding = Assert.Throws<SqliteException>(() => InsertPartner(server, "P802", parentId: middle));
        Assert.Contains("名寄せの親には、さらに親を持つ取引先を選べません。", adding.Message, StringComparison.Ordinal);

        // ②誰かの親になっている取引先に、親を付ける（UPDATE のトリガ）。
        var leaf = InsertPartner(server, "P803");
        var updating = Assert.Throws<SqliteException>(() => server.Execute(
            $"update partners set parent_partner_id = {leaf} where id = {root}"));
        Assert.Contains(
            "他の取引先の名寄せの親になっている取引先には、親を付けられません。",
            updating.Message,
            StringComparison.Ordinal);
    }

    private static long InsertPartner(
        PartnerServer server,
        string code = "P900",
        string? entityType = null,
        string? corporateNumber = null,
        long? parentId = null)
    {
        var type = entityType is null ? "null" : $"'{entityType}'";
        var number = corporateNumber is null ? "null" : $"'{corporateNumber}'";
        var parent = parentId is long p ? p.ToString(CultureInfo.InvariantCulture) : "null";

        server.Execute($"""
            insert into partners (code, name, is_active, entity_type, corporate_number, parent_partner_id)
            values ('{code}', '取引先 {code}', 1, {type}, {number}, {parent})
            """);

        return server.Scalar<long>("select last_insert_rowid()");
    }
}
