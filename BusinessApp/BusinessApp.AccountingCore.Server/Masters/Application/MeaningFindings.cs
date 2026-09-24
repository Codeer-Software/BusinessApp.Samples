namespace BusinessApp.AccountingCore.Server.Masters.Application;

using Codeer.LowCode.Blazor.Repository.Data;

/// <summary>
/// 意味の凍結（<see cref="MasterMeaningGate"/>）が見つけたもの——<b>断る理由</b>と、<b>変えられないと断った欄</b>。
/// </summary>
/// <remarks>
/// <para><b>欄を返すのは、<see cref="MasterSubmitGate"/> の値の検査がその欄を見ないためである</b>（docs/21 §2-6 の「前提の崩れた検査は飛ばす」）。
/// 変えられない欄の値を検査して「こう直せ」と言うと、<b>従っても通らない一手</b>を並べることになる。</para>
/// <para><b>行は参照で区別する。</b> 同じ保存に同じ識別子の行が 2 つ来る形（取込・API）でも、
/// どの差分を断ったかを取り違えないためである。<b>その形を撃つ検体は無い</b>——検体（<c>凍結した欄は同じ保存の別の行の検査を消さない</c>）が撃つのは、モジュール名で覚える誤実装までである。</para>
/// </remarks>
public sealed class MeaningFindings
{
    private readonly List<string> reasons = [];

    private readonly Dictionary<ModuleData, HashSet<string>> frozen = new(ReferenceEqualityComparer.Instance);

    /// <summary>断る理由。見つけた順。</summary>
    public IReadOnlyList<string> Reasons => reasons;

    /// <summary>その行で、変えられないと断った欄の名前（CLB のフィールド名）。無ければ空。</summary>
    public IReadOnlySet<string> FrozenFieldsOf(ModuleData row)
        => frozen.TryGetValue(row, out var fields) ? fields : EmptyFields;

    /// <summary>理由と、その理由で断った欄を足す。</summary>
    internal void Add(ModuleData row, IEnumerable<string> fieldNames, string reason)
    {
        reasons.Add(reason);

        if (!frozen.TryGetValue(row, out var fields))
        {
            fields = new HashSet<string>(StringComparer.Ordinal);
            frozen.Add(row, fields);
        }

        fields.UnionWith(fieldNames);
    }

    private static readonly IReadOnlySet<string> EmptyFields = new HashSet<string>(StringComparer.Ordinal);
}
