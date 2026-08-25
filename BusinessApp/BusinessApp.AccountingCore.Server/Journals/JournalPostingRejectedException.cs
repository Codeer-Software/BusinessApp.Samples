namespace BusinessApp.AccountingCore.Server.Journals;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上が検証で止められたときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para>違反を<b>全件</b>持って投げる。1 件だけ見せると、利用者は直しては弾かれを繰り返す。</para>
/// <para><b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。
/// qa/01 D-12）。改行を入れても表示に出ず、改行コードの管理だけが増える。
/// 代わりに<b>件数と番号</b>で区切る——1 行に繋がっても「あと何を直すか」が読み取れる。</para>
/// </remarks>
public sealed class JournalPostingRejectedException(IReadOnlyList<Violation> violations)
    : Exception(BuildMessage(violations))
{
    /// <summary>並べられる番号（それを超えたら番号なしで続ける）。</summary>
    private static readonly string[] Numbers = ["①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩"];

    public IReadOnlyList<Violation> Violations { get; } = violations;

    private static string BuildMessage(IReadOnlyList<Violation> violations)
    {
        var errors = violations.Where(v => v.Severity == ViolationSeverity.Error).ToList();
        var numbered = errors.Select((violation, index) => $"{Number(index)}{Describe(violation)}");

        // 1 件のときに「（1 件）」と数えて見せても、読み手の役に立たない。
        var count = errors.Count > 1 ? $"（{errors.Count} 件）" : string.Empty;
        return $"計上できません{count}。{string.Join(string.Empty, numbered)}";
    }

    /// <summary>番号。10 件を超えたら付けない（区切りは句点が担う）。</summary>
    private static string Number(int index)
        => index < Numbers.Length ? Numbers[index] : string.Empty;

    private static string Describe(Violation violation)
        => violation.LineNo is int lineNo ? $"{lineNo} 行目: {violation.Message}" : violation.Message;
}
