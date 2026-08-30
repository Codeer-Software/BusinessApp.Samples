namespace BusinessApp.AccountingCore.Server.Tests;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 自社情報を保存するときの関門（<see cref="CompanyProfileSubmitGate"/>）。
/// </summary>
/// <remarks>
/// <b>見るのは法人番号だけ</b>である。取引先の法人番号と<b>同じ判定</b>を通すことがこの関門の要点で、
/// 判定と文言は <see cref="BusinessApp.Partners.CorporateNumber"/> が 1 か所で持つ。
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
    [Fact]
    public async Task 空欄はNULLで保存する()
    {
        var save = new SaveSpy();
        var profile = Profile(corporateNumber: "   ");

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(profile)], save.SaveAsync);

        Assert.Null(((TextFieldData)profile.Fields["CorporateNumber"]).Value);
        Assert.True(save.Called);
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

    /// <summary>
    /// 法人番号の欄が<b>文字の欄でない</b>形で来ても落ちない。
    /// </summary>
    /// <remarks>
    /// CLB は宣言した型でしか送らないので画面からは来ないが、
    /// 型で分岐している以上、外れたときに例外ではなく素通しになることを固定しておく。
    /// </remarks>
    [Fact]
    public async Task 想定していない型の法人番号は素通しする()
    {
        var save = new SaveSpy();
        var profile = Profile();
        profile.Fields["CorporateNumber"] = new NumberFieldData { Value = 5835678256246m };

        await new CompanyProfileSubmitGate().SubmitAsync([Updating(profile)], save.SaveAsync);

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
}
