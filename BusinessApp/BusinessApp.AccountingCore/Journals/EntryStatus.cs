namespace BusinessApp.AccountingCore.Journals;

/// <summary>仕訳の状態（docs/10 §5）。</summary>
public enum EntryStatus
{
    /// <summary>下書き。まだ帳簿ではないので自由に編集・削除できる。</summary>
    Draft,

    /// <summary>計上済み。<b>変更も削除もできない</b>（I-05）。訂正・取消は反対仕訳で行う。</summary>
    Posted,
}

public static class EntryStatusExtensions
{
    /// <summary>
    /// 利用者に見せる名前。<b>列挙子をそのまま文言に混ぜない</b>（docs/21_画面の原則.md §2）。
    /// </summary>
    /// <remarks>
    /// CLB のデザイン enum（<c>Enums/EntryStatuses.enum.json</c>）と一致することを
    /// <c>EnumConsistencyTests</c> が検査する。
    /// </remarks>
    public static string DisplayName(this EntryStatus status) => status switch
    {
        EntryStatus.Draft => "下書き",
        EntryStatus.Posted => "計上済み",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "知らない仕訳の状態。"),
    };
}
