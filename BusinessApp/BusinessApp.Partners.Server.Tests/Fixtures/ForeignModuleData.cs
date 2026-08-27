namespace BusinessApp.Partners.Server.Tests.Fixtures;

using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 取引先部品の<b>外</b>にあるモジュールの保存。関門が素通しすべきものを作る。
/// </summary>
/// <remarks>
/// <para>関門は <c>ModuleName</c> で自分の担当かどうかを決めるので、
/// 他の部品のモジュールが同じ保存に混ざっても割り込んではいけない。
/// その検査に使う。</para>
/// <para><b>名前だけを真似て、会計コアを型では参照しない。</b>
/// 会計コアのモジュール名を文字列で持つのは、取引先部品が会計コアに依存しないためである
/// （ADR-0024 §4）。名前がずれても、この検査が確かめているのは
/// 「担当外のモジュールに割り込まない」ことなので意味は変わらない。</para>
/// </remarks>
internal static class ForeignModuleData
{
    /// <summary>会計コアの振替伝票のモジュール名。</summary>
    public const string JournalEntryModuleName = "JournalEntry";

    /// <summary>担当外のモジュールのデータ 1 件。関門は名前しか見ないので識別子だけ載せる。</summary>
    public static ModuleData Entry(string id)
    {
        var data = new ModuleData { Name = JournalEntryModuleName };
        data.Fields["Id"] = new IdFieldData { Value = id };
        return data;
    }

    /// <summary>担当外のモジュールだけを載せた 1 回の保存。</summary>
    public static ModuleSubmitData Adding(params ModuleData[] data)
        => new() { ModuleName = JournalEntryModuleName, Add = [.. data] };
}
