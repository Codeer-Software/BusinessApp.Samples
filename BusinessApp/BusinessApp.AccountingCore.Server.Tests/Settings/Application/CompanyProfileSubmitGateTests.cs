namespace BusinessApp.AccountingCore.Server.Tests.Settings.Application;

using BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;
using BusinessApp.AccountingCore.Server.Settings.Application;
using BusinessApp.AccountingCore.Settings;
using BusinessApp.ServerSupport;

/// <summary>
/// 自社情報を保存するときの関門（<see cref="CompanyProfileSubmitGate"/>）。
/// </summary>
/// <remarks>
/// <b>見るのは 4 つ</b>——決算月・文字の欄の上限（docs/12 §2-2）・郵便番号の書式・法人番号である。
/// <b>判定と文言はそれぞれの型が 1 か所で持つ</b>
/// （<see cref="BusinessApp.ServerSupport.MasterTextLength"/>・
/// <see cref="BusinessApp.AccountingCore.Settings.PostalCode"/>・
/// <see cref="BusinessApp.Partners.CorporateNumber"/>）。
/// ここが確かめるのは「その判定に通していること」と「空欄の扱い」である。
/// </remarks>
public class CompanyProfileSubmitGateTests
{
    /// <summary>国税庁の計算例で検査用数字が合う番号。</summary>
    private const string ValidNumber = "5835678256246";

    private sealed class SaveSpy
    {
        public bool Called { get; private set; }

        public Task<List<ModuleSubmitResult>> SaveAsync()
        {
            Called = true;
            return Task.FromResult(new List<ModuleSubmitResult>());
        }
    }

    /// <summary>CLB は<b>変更されたフィールドしか送ってこない</b>ので、渡された項目だけ載せる。</summary>
    private static ModuleData Profile(string? corporateNumber = null, string? name = null)
    {
        var data = new ModuleData { Name = CompanyProfileSubmitGate.ModuleName };

        if (corporateNumber is not null)
        {
            data.Fields["CorporateNumber"] = new TextFieldData { Value = corporateNumber };
        }

        if (name is not null)
        {
            data.Fields["Name"] = new TextFieldData { Value = name };
        }

        return data;
    }

    /// <summary>欄を 1 つだけ触った更新（実機で来る形。触った欄しか載らない）。</summary>
    private static ModuleData Field(string field, string? value)
    {
        var data = new ModuleData { Name = CompanyProfileSubmitGate.ModuleName };
        data.Fields[field] = new TextFieldData { Value = value };
        return data;
    }

    /// <summary>決算月だけを触った更新（実機で来る形。触った欄しか載らない）。</summary>
    private static ModuleData Month(decimal? month)
    {
        var data = new ModuleData { Name = CompanyProfileSubmitGate.ModuleName };
        data.Fields["FiscalYearEndMonth"] = new NumberFieldData { Value = month };
        return data;
    }

    private static ModuleSubmitData Updating(params ModuleData[] data)
        => new() { ModuleName = CompanyProfileSubmitGate.ModuleName, Update = [.. data] };

    private static ModuleSubmitData Adding(params ModuleData[] data)
        => new() { ModuleName = CompanyProfileSubmitGate.ModuleName, Add = [.. data] };

    [Fact]
    public async Task 検査用数字の合う法人番号は保存へ進む()
    {
        var save = new SaveSpy();

        await new CompanyProfileSubmitGate().SubmitAsync(
            [Updating(Profile(corporateNumber: ValidNumber))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>桁が足りない番号は書式で止まる。</summary>
    [Fact]
    public async Task 桁の足りない法人番号は止める()
    {
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => new CompanyProfileSubmitGate().SubmitAsync(
                [Updating(Profile(corporateNumber: "12345"))], save.SaveAsync));

        Assert.Contains("13 桁", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>桁は合っているが検査用数字が合わない番号を止める。</b>
    /// </summary>
    /// <remarks>
    /// ここが本題である。桁だけを見る関門は、打ち間違えた番号を全部通す——
    /// そして<b>自社の法人番号は帳簿と申告書に載る</b>ので、誤りが外へ出る。
    /// </remarks>
    [Fact]
    public async Task 検査用数字の合わない法人番号を止める()
    {
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => new CompanyProfileSubmitGate().SubmitAsync(
                [Updating(Profile(corporateNumber: "1835678256246"))], save.SaveAsync));

        Assert.Contains("打ち間違い", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>
    /// 空欄は通す。<b>ただし空文字ではなく <c>null</c> で保存する。</b>
    /// </summary>
    /// <remarks>
    /// DDL の <c>CHECK</c> は「NULL か、数字 13 桁」しか許さない。空文字を書き戻すと、
    /// <b>入っていた番号を消すという正規の直し方</b>が DB の失敗になる（取引先で実際に踏んだ）。
    /// </remarks>
    [Theory]
    [InlineData("")]        // 画面が実際に送る形（TextEditEmptyType: "StringEmpty"）
    [InlineData("   ")]     // 空白だけを打った・貼り付けた
    public async Task 空欄はNULLで保存する(string blank)
    {
        var save = new SaveSpy();
        var profile = Profile(corporateNumber: blank);

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(profile)], save.SaveAsync);

        Assert.Null(((TextFieldData)profile.Fields["CorporateNumber"]).Value);
        Assert.True(save.Called);
    }

    /// <summary>
    /// <b>関門が通した値は、DB も受け取れる。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>関門は「DB に拒まれる値を保存へ渡さない」ところまで責任を持つ</b>（qa/03 L-14）。
    /// 通しただけで DB が拒むと、利用者には生の SQLite のメッセージが出る（qa/01 F-16）。</para>
    /// <para><b>ここは往復で見る。</b> <c>company_profile.corporate_number</c> には
    /// <c>partners</c> と違って <c>CHECK</c> が無い（qa/02 R25-11 で揃える）ので、
    /// <b>DB 側の砦が無いぶん、通した値が本当に書けることを見ておく価値がある。</b></para>
    /// </remarks>
    [Theory]
    [InlineData(ValidNumber)]
    [InlineData(null)]
    public async Task 関門が通した値はDBも受け取れる(string? stored)
    {
        using var server = new AccountingServer();
        var save = new SaveSpy();
        var profile = Profile(corporateNumber: stored ?? string.Empty);

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(profile)], save.SaveAsync);

        // 関門が書き換えた**そのままの値**を DB へ入れる。
        var written = ((TextFieldData)profile.Fields["CorporateNumber"]).Value;
        server.Execute(
            written is null
                ? "update company_profile set corporate_number = null where id = 1"
                : $"update company_profile set corporate_number = '{written}' where id = 1");

        Assert.Equal(stored, server.Scalar<string?>("select corporate_number from company_profile"));
    }

    /// <summary>貼り付けで紛れ込んだ空白は落として保存する。</summary>
    [Fact]
    public async Task 前後の空白を落として保存する()
    {
        var save = new SaveSpy();
        var profile = Profile(corporateNumber: $" {ValidNumber} ");

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(profile)], save.SaveAsync);

        Assert.Equal(ValidNumber, ((TextFieldData)profile.Fields["CorporateNumber"]).Value);
    }

    /// <summary>法人番号が差分に無ければ検査しない（社名だけを直した保存）。</summary>
    [Fact]
    public async Task 法人番号が差分に無ければ検査しない()
    {
        var save = new SaveSpy();

        await new CompanyProfileSubmitGate().SubmitAsync(
            [Updating(Profile(name: "株式会社アルタイルシステムズ"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>追加でも見る（初期データを入れ直した環境で穴にしない）。</summary>
    [Fact]
    public async Task 追加の自社情報も見る()
    {
        var save = new SaveSpy();

        await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => new CompanyProfileSubmitGate().SubmitAsync(
                [Adding(Profile(corporateNumber: "1835678256246"))], save.SaveAsync));

        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>入れ物の名前ではなく、中身の名前で担当を決める。</b>
    /// </summary>
    /// <remarks>
    /// <c>ModuleSubmitData.ModuleName</c> で絞る実装だと、別モジュールの保存に混ざった
    /// 自社情報を見落とす（qa/02 R16-16 と同じ型）。
    /// </remarks>
    [Fact]
    public async Task 入れ物が別モジュールでも中の自社情報は見る()
    {
        var save = new SaveSpy();
        var submit = new ModuleSubmitData
        {
            ModuleName = "JournalEntry",
            Update = [Profile(corporateNumber: "1835678256246")],
        };

        await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => new CompanyProfileSubmitGate().SubmitAsync([submit], save.SaveAsync));

        Assert.False(save.Called);
    }

    /// <summary>担当外のモジュールの保存には割り込まない。</summary>
    [Fact]
    public async Task 別のモジュールの保存は素通しする()
    {
        var save = new SaveSpy();
        var other = new ModuleData { Name = "JournalEntry" };
        other.Fields["CorporateNumber"] = new TextFieldData { Value = "12345" };

        await new CompanyProfileSubmitGate().SubmitAsync(
            [new ModuleSubmitData { ModuleName = "JournalEntry", Update = [other] }], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>引数を渡さなければ止まる。<b>引数名まで表明する。</b></summary>
    [Fact]
    public async Task 引数を渡さなければ止まる()
    {
        var gate = new CompanyProfileSubmitGate();

        var missingData = await Assert.ThrowsAsync<ArgumentNullException>(
            () => gate.SubmitAsync(null!, () => Task.FromResult(new List<ModuleSubmitResult>())));
        Assert.Equal("transactionData", missingData.ParamName);

        var missingSave = await Assert.ThrowsAsync<ArgumentNullException>(
            () => gate.SubmitAsync([], null!));
        Assert.Equal("save", missingSave.ParamName);
    }

    // --- 決算月（qa/03 L-28 の 3 例目） -------------------------------------------

    /// <summary>
    /// 月でない決算月は、利用者の語で断る。
    /// </summary>
    /// <remarks>
    /// <b>13 を入れると定型文になっていた</b>（qa/03 L-28。2026-09-04 の探索的テストで実測）。
    /// DDL の <c>CHECK</c> は止めるが、そこまで進むと利用者に見えるのは DB の失敗である。
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    [InlineData(1.5)]
    public async Task 月でない決算月は断る(double month)
    {
        var save = new SaveSpy();
        var gate = new CompanyProfileSubmitGate();

        var thrown = await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => gate.SubmitAsync([Updating(Month((decimal)month))], save.SaveAsync));

        Assert.Contains("「決算月」は 1 から 12 までの整数で入れてください", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public async Task 月として正しい決算月は保存へ進む(int month)
    {
        var save = new SaveSpy();

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(Month(month))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>決算月を触っていない保存は、決算月を見ない（差分に載らない欄は「変えていない」）。</summary>
    [Fact]
    public async Task 決算月を触っていなければ見ない()
    {
        var save = new SaveSpy();

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(Profile(name: "株式会社アルタイル"))], save.SaveAsync);

        Assert.True(save.Called);
    }

    /// <summary>
    /// 決算月を空にした保存も、利用者の語で断る。
    /// </summary>
    /// <remarks>
    /// <b>2026-09-09 の自己レビューで揃えた。</b> DB の <c>NOT NULL</c> に投げると定型文になり、
    /// <b>「13 は利用者の語で断るのに、空は枠組みの言葉」という食い違いが同じ欄で起きる</b>。
    /// </remarks>
    [Fact]
    public async Task 決算月が空なら断る()
    {
        var save = new SaveSpy();
        var gate = new CompanyProfileSubmitGate();

        var thrown = await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => gate.SubmitAsync([Updating(Month(null))], save.SaveAsync));

        Assert.Contains("「決算月」を入れてください", thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>決算月の欄が数値でない要求も断る（検査できない値を通すのは fail-open である）。</summary>
    [Fact]
    public async Task 決算月の欄が数値でなければ断る()
    {
        var save = new SaveSpy();
        var data = new ModuleData { Name = CompanyProfileSubmitGate.ModuleName };
        data.Fields["FiscalYearEndMonth"] = new TextFieldData { Value = "3" };

        await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => new CompanyProfileSubmitGate().SubmitAsync([Updating(data)], save.SaveAsync));

        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>文字の欄は、呼び名といまの文字数で断る</b>（docs/12 §2-2。旧 Q-26 の決定。2026-09-16）。
    /// </summary>
    /// <remarks>
    /// <b>欄ごとに上限が違う</b>ので、<b>5 欄とも撃つ</b>——1 つにまとめると、
    /// 表から 1 行落としても緑のままになる。
    /// </remarks>
    [Theory]
    [InlineData("Name", "会社名", 100)]
    [InlineData("NameKana", "カナ", 200)]
    [InlineData("RepresentativeName", "代表者名", 30)]
    [InlineData("Address", "住所", 200)]
    [InlineData("PhoneNumber", "電話番号", 20)]
    public async Task 長すぎる文字の欄は呼び名といまの文字数で断る(string field, string label, int max)
    {
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => new CompanyProfileSubmitGate().SubmitAsync(
                [Updating(Field(field, new string('あ', max + 1)))], save.SaveAsync));

        // **接頭の「保存できません。」はこの規則の持ち物ではない**ので、末尾だけを見る
        // （`MasterSubmitGateTests` と同じ作法）。
        Assert.EndsWith(
            $"「{label}」は {max} 文字以内です。いまは {max + 1} 文字あります。短くして入力し直してください。",
            thrown.Message,
            StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>上限ちょうどは通り、前後の空白は落として保存する。</b>
    /// </summary>
    /// <remarks>
    /// <b>両端を撃たないと <c>&gt;</c> と <c>&gt;=</c> の取り違えが見えない。</b>
    /// <b>落とした姿を差分に書き戻す</b>——比べるときだけ落とすと、
    /// 関門が数えた長さと DDL が数える長さが食い違う（qa/03 の L-14）。
    /// </remarks>
    [Theory]
    [InlineData("Name", 100)]
    [InlineData("NameKana", 200)]
    [InlineData("RepresentativeName", 30)]
    [InlineData("Address", 200)]
    [InlineData("PhoneNumber", 20)]
    public async Task 上限ちょうどは通り前後の空白は落ちる(string field, int max)
    {
        var save = new SaveSpy();
        var data = Field(field, "  " + new string('あ', max) + "  ");

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(data)], save.SaveAsync);

        Assert.True(save.Called);
        Assert.Equal(new string('あ', max), ((TextFieldData)data.Fields[field]).Value);
    }

    /// <summary>
    /// <b>読めない型の欄は断る</b>（<c>MasterSubmitGate</c> と同じ。fail-open にしない）。
    /// </summary>
    /// <remarks>
    /// <b><c>as</c> で <c>null</c> に落とすと「触られていない」と見分けが付かず、
    /// 欄の型を変えた日に、その欄を見る検査がまとめて素通しへ落ちる</b>
    /// （<see cref="UnreadableFieldException"/> の注記。2026-09-16 に 3 つとも揃えた）。
    /// <b>これは利用者の誤りではない</b>ので、文言はホストが定型文へ差し替える。
    /// </remarks>
    [Theory]
    [InlineData("Name")]
    [InlineData("PostalCode")]
    [InlineData("CorporateNumber")]
    public async Task 読めない型の欄は断る(string field)
    {
        var save = new SaveSpy();
        var data = new ModuleData { Name = CompanyProfileSubmitGate.ModuleName };
        data.Fields[field] = new NumberFieldData { Value = 1m };

        var thrown = await Assert.ThrowsAsync<UnreadableFieldException>(
            () => new CompanyProfileSubmitGate().SubmitAsync([Updating(data)], save.SaveAsync));

        Assert.Equal(CompanyProfileSubmitGate.ModuleName, thrown.Module);
        Assert.Equal(field, thrown.Field);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>写した呼び名と列が、デザイン JSON と一致する</b>（docs/20 §4 の「已むを得ない重複」）。
    /// </summary>
    /// <remarks>
    /// <b>ラベルは差し戻しの文言に出る。</b> ずれると、画面に無い語を名指しして
    /// 「どの欄のことか」が分からなくなる。
    /// <b>列も見る</b>——ずれると、DDL のトリガが画面の書かない列を守り続ける。
    /// <c>MasterSubmitGateTests.写した表とラベルはデザインと一致する</c> と同じ形である
    /// （2026-09-16 の自己レビューで、自社情報に無いことを 2 人に指摘された）。
    /// </remarks>
    [Fact]
    public void 写した呼び名と列はデザインと一致する()
    {
        var path = Directory
            .EnumerateFiles(
                BusinessApp.TestSupport.TestDatabase.ModulesDirectory,
                $"{CompanyProfileSubmitGate.ModuleName}.mod.json",
                SearchOption.AllDirectories)
            .Single();
        using var design = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var fields = design.RootElement.GetProperty("Fields");

        Assert.Equal("company_profile", design.RootElement.GetProperty("DbTable").GetString());
        Assert.NotEmpty(CompanyProfileSubmitGate.TextFields);

        foreach (var (field, column, label, _) in CompanyProfileSubmitGate.TextFields)
        {
            var design_ = fields.EnumerateArray().Single(f => f.GetProperty("Name").GetString() == field);
            Assert.Equal(column, design_.GetProperty("DbColumn").GetString());
            Assert.Equal(label, design_.GetProperty("DisplayName").GetString());
        }

        // **書式の欄も同じ写しである**（郵便番号・法人番号）。
        foreach (var (field, label) in new[]
                 {
                     ("PostalCode", PostalCode.Label),
                     ("CorporateNumber", BusinessApp.Partners.CorporateNumber.Label),
                 })
        {
            var design_ = fields.EnumerateArray().Single(f => f.GetProperty("Name").GetString() == field);
            Assert.Equal(label, design_.GetProperty("DisplayName").GetString());
        }
    }

    /// <summary>
    /// <b>郵便番号は書式で断る</b>（旧 Q-26 の決定。2026-09-16）。
    /// </summary>
    /// <remarks>
    /// <b>判定と文言は <see cref="BusinessApp.AccountingCore.Settings.PostalCode"/> が持つ。</b>
    /// ここが確かめるのは「その判定に通していること」である。
    /// </remarks>
    [Theory]
    [InlineData("1234567")]
    [InlineData("123-456")]
    [InlineData("１23-4567")]
    public async Task 書式の合わない郵便番号は止める(string value)
    {
        var save = new SaveSpy();

        var thrown = await Assert.ThrowsAsync<CompanyProfileRejectedException>(
            () => new CompanyProfileSubmitGate().SubmitAsync(
                [Updating(Field("PostalCode", value))], save.SaveAsync));

        Assert.EndsWith(PostalCode.DescribeProblem(value)!, thrown.Message, StringComparison.Ordinal);
        Assert.False(save.Called);
    }

    /// <summary>
    /// <b>書式の合う郵便番号は通り、前後の空白は落として保存する。</b>
    /// </summary>
    [Fact]
    public async Task 書式の合う郵便番号は通る()
    {
        var save = new SaveSpy();
        var data = Field("PostalCode", "  123-4567  ");

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(data)], save.SaveAsync);

        Assert.True(save.Called);
        Assert.Equal("123-4567", ((TextFieldData)data.Fields["PostalCode"]).Value);
    }

    /// <summary>
    /// <b>空欄の郵便番号は NULL に倒す</b>（法人番号と同じ作法）。
    /// </summary>
    /// <remarks>
    /// <b>空文字と「入っていない」を DB で区別させない</b>——
    /// 区別が付かない 2 通りの「空」が混ざると、突合も表示も両方を扱うことになる（docs/20 §7）。
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task 空欄の郵便番号は_NULL_に倒す(string? value)
    {
        var save = new SaveSpy();
        var data = Field("PostalCode", value);

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(data)], save.SaveAsync);

        Assert.True(save.Called);
        Assert.Null(((TextFieldData)data.Fields["PostalCode"]).Value);
    }

}
