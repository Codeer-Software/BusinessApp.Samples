namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 計上の結果。
/// </summary>
/// <remarks>
/// <b>違反が空かどうかで判定しない。</b> 警告は返るが計上はできるので、
/// 計上できたかは <see cref="IsPosted"/> で見る。
/// </remarks>
/// <param name="Violations">見つかった違反（警告を含む）。</param>
/// <param name="PostedEntry">計上できたときの伝票。できなかったときは null。</param>
/// <param name="NextSequence">採番を進めた後の状態。計上できなかったときは null。</param>
public sealed record PostingResult(
    IReadOnlyList<Violation> Violations,
    JournalEntry? PostedEntry = null,
    EntryNumberSequence? NextSequence = null)
{
    public bool IsPosted => PostedEntry is not null;
}
