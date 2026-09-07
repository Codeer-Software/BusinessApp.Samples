namespace BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// CLB が保存時に送ってくる形（<see cref="ModuleSubmitData"/>）を組み立てる。
/// </summary>
/// <remarks>
/// <para><b>実測した形をそのまま真似る</b>（qa/01 F-11・F-12）。
/// <list type="bullet">
///   <item>伝票と明細は同じ <see cref="ModuleSubmitData"/> の <c>Add</c> / <c>Update</c> に混ざる</item>
///   <item><c>ModuleData</c> には<b>変更されたフィールドしか入らない</b></item>
/// </list></para>
/// <para><b>新規（<c>Add</c>）と更新（<c>Update</c>）で形が違うので、作る口を分けてある。</b>
/// 新規は画面が初期値を入れるので必須項目が揃って届き（<see cref="NewEntry"/> / <see cref="Line"/>）、
/// 更新は<b>触った項目しか載らない</b>（<see cref="Entry"/> / <see cref="LineChanging"/>）。
/// <b>どちらか一方の形だけでテストを書くと、片方の経路がまるごと未検査になる。</b>
/// 「全フィールドが揃っている」都合のよい形を更新にも使うと、
/// テストは通るのに実機で落ちる、という最悪の組み合わせになる。</para>
/// </remarks>
internal static class SubmitData
{
    /// <summary>1 つの保存。<c>Add</c> に載せる。</summary>
    public static ModuleSubmitData Adding(params ModuleData[] data)
        => new() { ModuleName = "JournalEntry", Add = [.. data] };

    /// <summary>1 つの保存。<c>Update</c> に載せる。</summary>
    public static ModuleSubmitData Updating(params ModuleData[] data)
        => new() { ModuleName = "JournalEntry", Update = [.. data] };

    /// <summary>
    /// 1 つの保存。<c>Delete</c> に載せる。
    /// </summary>
    /// <remarks>
    /// <b>削除だけ器が違う。</b> 追加・更新はフィールドの束（<see cref="ModuleData"/>）で来るが、
    /// 削除は識別子とモジュール名だけの <see cref="ModuleDeleteInfo"/> で来る。
    /// <b>ここを ModuleData で作ると、関門が本番で見ている場所を 1 度も通らない。</b>
    /// </remarks>
    public static ModuleSubmitData Deleting(params string[] ids)
        => new()
        {
            ModuleName = "JournalEntry",
            Delete = [.. ids.Select(id => new ModuleDeleteInfo { Id = id, ModuleName = "JournalEntry" })],
        };

    /// <summary>
    /// <b>更新の</b>伝票。<c>Id</c> と、指定した状態だけを載せる（触っていない項目は差分に無い）。
    /// </summary>
    /// <remarks>
    /// 「開いて計上ボタンを押すだけ」という、いちばん多い保存の形である。
    /// <paramref name="status"/> が null なら状態も載せない。
    /// </remarks>
    public static ModuleData Entry(string id, string? status = null)
    {
        var data = new ModuleData { Name = "JournalEntry" };
        data.Fields["Id"] = new IdFieldData { Value = id };

        // **摘要を載せる。** 計上には要る（docs/10 §4-2-1）ので、載せない差分は関門が差し戻す——
        // それを試すテストは NewEntryWithout(id, "Description") と書く。
        // **他の検体と別の字**にしてある（qa/03 L-02 の縮退）。
        data.Fields["Description"] = new TextFieldData { Value = DefaultDescription };
        if (status is not null)
        {
            data.Fields["Status"] = new SelectFieldData { Value = status };
        }

        return data;
    }

    /// <summary>
    /// <b>新規作成の</b>伝票。<b>DDL の <c>NOT NULL</c> に当たる項目を載せる。</b>
    /// </summary>
    /// <remarks>
    /// 画面は <c>Detail_OnAfterInitialization</c> で取引日・計上日・会計年度・状態・種別を入れるので、
    /// 新規の差分にはこれらが必ず載る。<b>載せずに新規を作ると保存が DB に拒まれる</b>ので、
    /// 関門（<c>JournalSubmitRequirements</c>）はここが欠けた新規を差し戻す。
    /// </remarks>
    public static ModuleData NewEntry(string id, string? status = null)
    {
        var data = Entry(id, status);
        data.Fields["TransactionDate"] = new DateFieldData { Value = new DateOnly(2026, 8, 24) };
        data.Fields["PostingDate"] = new DateFieldData { Value = new DateOnly(2026, 8, 24) };
        data.Fields["FiscalYear"] = new LinkFieldData { Value = "1" };
        return data;
    }

    /// <summary>保存の差分に載る摘要。<b>層ごとに別の字にしてある</b>（qa/03 L-02）。</summary>
    public const string DefaultDescription = "7 月分の水道光熱費";

    /// <summary>項目を 1 つ<b>差分から落とした</b>新規の伝票。</summary>
    public static ModuleData NewEntryWithout(string id, string fieldName)
    {
        var data = NewEntry(id, status: "draft");
        data.Fields.Remove(fieldName);
        return data;
    }

    /// <summary>
    /// <b>新規作成の</b>明細。<b>DDL の <c>NOT NULL</c> に当たる項目を載せる。</b>
    /// </summary>
    /// <remarks>
    /// <para>値は<b>全部違うものにしてある</b>（行番号 3・金額 1,234・科目 7・税区分 5）。
    /// 同じ値を並べると、書き込みが 2 つの列を取り違えても気づけない（qa/03 L-02）。
    /// <b>識別子は仮のまま</b>で届く（qa/01 C-08）。</para>
    /// <para>勘定科目と税区分が<b>実在するか</b>はここでは見ない（外部キーの検査は計上の検証が持つ）。
    /// 実際に DB へ書く往復のテストは、コードから識別子を引き直す。</para>
    /// </remarks>
    public static ModuleData Line(int lineNo = 3)
    {
        var data = new ModuleData { Name = "JournalLine" };
        data.Fields["Id"] = new IdFieldData { Value = $"@temporary:line{lineNo}" };
        data.Fields["LineNo"] = new NumberFieldData { Value = lineNo };
        data.Fields["DebitCredit"] = new SelectFieldData { Value = "debit" };
        data.Fields["Account"] = new LinkFieldData { Value = "7" };
        data.Fields["Amount"] = new NumberFieldData { Value = 1234 };
        data.Fields["TaxCategory"] = new LinkFieldData { Value = "5" };
        return data;
    }

    /// <summary>項目を 1 つ<b>差分から落とした</b>新規の明細（画面で一度も触っていない項目を表す）。</summary>
    public static ModuleData LineWithout(int lineNo, string fieldName)
    {
        var data = Line(lineNo);
        data.Fields.Remove(fieldName);
        return data;
    }

    /// <summary>項目を 1 つ<b>差し替えた</b>新規の明細（空にする・壊れた値を入れる・型を間違える）。</summary>
    public static ModuleData LineWith(int lineNo, string fieldName, FieldDataBase value)
    {
        var data = Line(lineNo);
        data.Fields[fieldName] = value;
        return data;
    }

    /// <summary>
    /// <b>更新の</b>明細。<c>Id</c> と、触った 1 項目だけを載せる。
    /// </summary>
    /// <remarks>
    /// <b>これが実機で届く更新の形である</b>（qa/01 F-12）——明細を 1 か所直した保存では、
    /// 他の項目も、同じ伝票の他の行も差分に載らない。
    /// </remarks>
    public static ModuleData LineChanging(string id, string fieldName, FieldDataBase value)
    {
        var data = new ModuleData { Name = "JournalLine" };
        data.Fields["Id"] = new IdFieldData { Value = id };
        data.Fields[fieldName] = value;
        return data;
    }

    /// <summary>
    /// 保存結果。<b>本物の CLB は「送った仮 ID → 採番された本物の ID」を
    /// <c>SourceId</c> / <c>DestinationId</c> で返す</b>（qa/03 L-10）。
    /// </summary>
    public static ModuleSubmitResult Result(string temporary, string real)
        => new() { SourceId = temporary, DestinationId = real };

    /// <summary>
    /// 保存が失敗した結果。<b>CLB は保存の失敗を例外ではなくこの項目に詰めて返す。</b>
    /// </summary>
    public static ModuleSubmitResult Failure(string message)
        => new() { ExceptionMessage = message };

    public static string? SelectValue(ModuleData data, string name)
        => (data.Fields[name] as SelectFieldData)?.Value;

    public static DateTime? DateTimeValue(ModuleData data, string name)
        => (data.Fields[name] as DateTimeFieldData)?.Value;
}
