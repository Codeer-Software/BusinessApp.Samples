namespace BusinessApp.AccountingCore.Server.Journals.Application;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 利用者の操作を差し戻すときの例外。保存全体を巻き戻す。
/// </summary>
/// <remarks>
/// <para><b>計上を止めるときだけではない。</b> 保存そのものを止める関門
/// （<see cref="JournalSubmitRequirements"/>）も同じ型を投げ、見出しだけを
/// <see cref="SavingHeadline"/> に替える。<b>型の名前は計上のときの用途で付いた</b>もので、
/// いまは「仕訳まわりの差し戻し」全体を運ぶ。</para>
/// <para>違反を<b>全件</b>持って投げる。1 件だけ見せると、利用者は直しては弾かれを繰り返す。</para>
/// <para><b>文言に改行を入れない。</b> トースト内の文字列は改行できない（CLB の仕様。
/// qa/01 D-12）。改行を入れても表示に出ず、改行コードの管理だけが増える。
/// 代わりに<b>件数と番号</b>で区切る——1 行に繋がっても「あと何を直すか」が読み取れる。</para>
/// </remarks>
public sealed class JournalPostingRejectedException(
    IReadOnlyList<Violation> violations, string headline, Exception? inner = null)
    : Exception(BuildMessage(violations, headline), inner)
{
    /// <summary>計上を止めたときの見出し。</summary>
    public const string PostingHeadline = "計上できません";

    /// <summary>
    /// 保存そのものを止めたときの見出し。
    /// </summary>
    /// <remarks>
    /// <b>下書き保存でも走る関門がある</b>（<see cref="JournalSubmitRequirements"/>）。
    /// そこで「計上できません」と言うと、計上していない操作を計上の言葉で断ることになる。
    /// </remarks>
    public const string SavingHeadline = "保存できません";

    /// <summary>「取り消す」を止めたときの見出し。</summary>
    /// <remarks>
    /// <b>見出しは押したボタンで決まる</b>（qa/02 R24-23）。取消の途中では計上も保存も走るが、
    /// 利用者がしたのは「取り消す」1 つなので、どこで捕まえても取消の言葉で断る。
    /// </remarks>
    public const string ReversalHeadline = "取り消せません";

    /// <summary>「訂正する」を止めたときの見出し。<see cref="ReversalHeadline"/> と同じ理由。</summary>
    public const string CorrectionHeadline = "訂正できません";

    /// <summary>「削除」を止めたときの見出し。<see cref="ReversalHeadline"/> と同じ理由。</summary>
    public const string DeletionHeadline = "削除できません";

    /// <summary>「複製」を止めたときの見出し。<see cref="ReversalHeadline"/> と同じ理由。</summary>
    public const string DuplicationHeadline = "複製できません";

    /// <summary>並べられる番号（それを超えたら番号なしで続ける）。</summary>
    private static readonly string[] Numbers = ["①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧", "⑨", "⑩"];

    /// <summary>計上を止める。<b>既定の見出しはこちら</b>で、呼び出しの大半がこの形である。</summary>
    public JournalPostingRejectedException(IReadOnlyList<Violation> violations)
        : this(violations, PostingHeadline)
    {
    }

    public IReadOnlyList<Violation> Violations { get; } = violations;

    private static string BuildMessage(IReadOnlyList<Violation> violations, string headline)
    {
        var errors = violations.Where(v => v.Severity == ViolationSeverity.Error).ToList();
        var numbered = errors.Select((violation, index) => $"{Number(index)}{Describe(violation)}");

        // 1 件のときに「（1 件）」と数えて見せても、読み手の役に立たない。
        var count = errors.Count > 1 ? $"（{errors.Count} 件）" : string.Empty;
        return $"{headline}{count}。{string.Join(string.Empty, numbered)}";
    }

    /// <summary>番号。10 件を超えたら付けない（区切りは句点が担う）。</summary>
    private static string Number(int index)
        => index < Numbers.Length ? Numbers[index] : string.Empty;

    private static string Describe(Violation violation)
        => violation.LineNo is int lineNo ? $"{lineNo} 行目: {violation.Message}" : violation.Message;
}
