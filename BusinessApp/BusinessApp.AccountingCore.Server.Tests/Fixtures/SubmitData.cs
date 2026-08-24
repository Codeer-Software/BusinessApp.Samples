namespace BusinessApp.AccountingCore.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// CLB が保存時に送ってくる形（<see cref="ModuleSubmitData"/>）を組み立てる。
/// </summary>
/// <remarks>
/// <b>実測した形をそのまま真似る</b>（qa/01 F-11・F-12）。
/// <list type="bullet">
///   <item>伝票と明細は同じ <see cref="ModuleSubmitData"/> の <c>Add</c> / <c>Update</c> に混ざる</item>
///   <item><c>ModuleData</c> には<b>変更されたフィールドしか入らない</b></item>
/// </list>
/// ここで「全フィールドが揃っている」都合のよい形を作ってしまうと、
/// テストは通るのに実機で落ちる、という最悪の組み合わせになる。
/// </remarks>
internal static class SubmitData
{
    /// <summary>1 つの保存。伝票を <c>Add</c> に載せる。</summary>
    public static ModuleSubmitData Adding(params ModuleData[] data)
        => new() { ModuleName = "JournalEntry", Add = [.. data] };

    /// <summary>1 つの保存。伝票を <c>Update</c> に載せる。</summary>
    public static ModuleSubmitData Updating(params ModuleData[] data)
        => new() { ModuleName = "JournalEntry", Update = [.. data] };

    /// <summary>伝票。<paramref name="status"/> が null なら状態を差分に載せない。</summary>
    public static ModuleData Entry(string id, string? status = null)
    {
        var data = new ModuleData { Name = "JournalEntry" };
        data.Fields["Id"] = new IdFieldData { Value = id };
        if (status is not null)
        {
            data.Fields["Status"] = new SelectFieldData { Value = status };
        }

        return data;
    }

    /// <summary>明細。関門は伝票しか見ないので、行番号だけあればよい。</summary>
    public static ModuleData Line(int lineNo)
    {
        var data = new ModuleData { Name = "JournalLine" };
        data.Fields["LineNo"] = new NumberFieldData { Value = lineNo };
        return data;
    }

    /// <summary>保存結果。仮 ID から本物の ID への対応表を持つ。</summary>
    public static ModuleSubmitResult Result(params (string Temporary, string Real)[] idMap)
    {
        var result = new ModuleSubmitResult();
        foreach (var (temporary, real) in idMap)
        {
            result.TemporaryIdMap[temporary] = real;
        }

        return result;
    }

    public static string? SelectValue(ModuleData data, string name)
        => (data.Fields[name] as SelectFieldData)?.Value;

    public static DateTime? DateTimeValue(ModuleData data, string name)
        => (data.Fields[name] as DateTimeFieldData)?.Value;
}
