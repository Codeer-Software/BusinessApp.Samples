namespace BusinessApp.AccountingCore.Server.Tests;

using BusinessApp.AccountingCore.Server;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 空にした文字の欄は、空文字ではなく NULL で保存する（docs/04 §1 の A-5）。
/// </summary>
/// <remarks>
/// <b>見た目は空なのに空値検索が取りこぼす</b>のを止めるための正規化である
/// （帳簿の空値検索は <c>IS NULL OR = ''</c> しか見ないので、空白だけの値はどちらにも当たらない）。
/// </remarks>
public class BlankTextNormalizerTests
{
    private static ModuleData Row(string name, params (string Field, FieldDataBase Value)[] fields)
    {
        var data = new ModuleData { Name = name };
        foreach (var (field, value) in fields)
        {
            data.Fields[field] = value;
        }

        return data;
    }

    private static string? ValueOf(ModuleData data, string field)
        => ((TextFieldData)data.Fields[field]).Value;

    /// <summary>
    /// 空白だけの値は、どの空白文字でも NULL になる。
    /// </summary>
    /// <remarks>
    /// <b>検体に NULL 可の列を選ぶ</b>（<c>accounts.name_kana</c>）。
    /// <c>accounts.name</c> のような <c>NOT NULL</c> の列で表明すると、
    /// <b>「空の名前を NULL で保存するのが正しい」と読める</b>——実際には DB が拒み、
    /// 利用者には定型文が出る（qa/03 L-28 の型。2026-09-09 の自己レビュー）。
    /// <b>必須の欄が空のまま届く経路は、この正規化ではなく欄ごとの関門が断る。</b>
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("　")]              // 全角空白
    [InlineData(" \t　\r\n ")]      // 混ぜても同じ
    public void 空白だけの値は_NULL_になる(string value)
    {
        var row = Row("Account", ("NameKana", new TextFieldData { Value = value }));

        BlankTextNormalizer.ToNull([new ModuleSubmitData { ModuleName = "Account", Update = [row] }]);

        Assert.Null(ValueOf(row, "NameKana"));
    }

    /// <summary><b>字が 1 つでもあれば触らない。</b> 前後の空白も落とさない——落とすのは欄ごとの関門の仕事である。</summary>
    [Theory]
    [InlineData("現金")]
    [InlineData(" 現金 ")]
    [InlineData("0")]
    public void 字がある値は触らない(string value)
    {
        var row = Row("Account", ("NameKana", new TextFieldData { Value = value }));

        BlankTextNormalizer.ToNull([new ModuleSubmitData { ModuleName = "Account", Update = [row] }]);

        Assert.Equal(value, ValueOf(row, "NameKana"));
    }

    /// <summary>もともと NULL の値は NULL のまま。</summary>
    [Fact]
    public void すでに_NULL_の値はそのまま()
    {
        var row = Row("Account", ("NameKana", new TextFieldData { Value = null }));

        BlankTextNormalizer.ToNull([new ModuleSubmitData { ModuleName = "Account", Update = [row] }]);

        Assert.Null(ValueOf(row, "NameKana"));
    }

    /// <summary>文字以外の欄は触らない。</summary>
    [Fact]
    public void 文字でない欄は触らない()
    {
        var row = Row(
            "JournalLine",
            ("Amount", new NumberFieldData { Value = 0 }),
            ("Account", new LinkFieldData { Value = string.Empty }),
            ("DebitCredit", new SelectFieldData { Value = string.Empty }));

        BlankTextNormalizer.ToNull([new ModuleSubmitData { ModuleName = "JournalLine", Update = [row] }]);

        Assert.Equal(0, ((NumberFieldData)row.Fields["Amount"]).Value);
        Assert.Equal(string.Empty, ((LinkFieldData)row.Fields["Account"]).Value);
        Assert.Equal(string.Empty, ((SelectFieldData)row.Fields["DebitCredit"]).Value);
    }

    /// <summary>
    /// <b>追加も更新も見る。</b>
    /// </summary>
    /// <remarks>
    /// 追加だけを寄せると、正しく作ってから空白で上書きする経路が残る
    /// （関門が追加と更新の両方を見るのと同じ理由）。
    /// </remarks>
    [Fact]
    public void 追加も更新も見る()
    {
        var added = Row("Account", ("NameKana", new TextFieldData { Value = "   " }));
        var updated = Row("Account", ("NameKana", new TextFieldData { Value = "\t" }));

        BlankTextNormalizer.ToNull(
            [new ModuleSubmitData { ModuleName = "Account", Add = [added], Update = [updated] }]);

        Assert.Null(ValueOf(added, "NameKana"));
        Assert.Null(ValueOf(updated, "NameKana"));
    }

    /// <summary>1 つの保存に混ざった別モジュールの行も、まとめて寄せる（親子は同じ器で届く。qa/01 F-11）。</summary>
    [Fact]
    public void 混ざった別モジュールの行もまとめて寄せる()
    {
        var entry = Row("JournalEntry", ("Description", new TextFieldData { Value = "　" }));
        var line = Row("JournalLine", ("ItemDescription", new TextFieldData { Value = " " }));

        BlankTextNormalizer.ToNull(
            [new ModuleSubmitData { ModuleName = "JournalEntry", Update = [entry, line] }]);

        Assert.Null(ValueOf(entry, "Description"));
        Assert.Null(ValueOf(line, "ItemDescription"));
    }

    [Fact]
    public void 引数を渡さなければ止まる()
        => Assert.Equal(
            "transactionData",
            Assert.Throws<ArgumentNullException>(() => BlankTextNormalizer.ToNull(null!)).ParamName);
}
