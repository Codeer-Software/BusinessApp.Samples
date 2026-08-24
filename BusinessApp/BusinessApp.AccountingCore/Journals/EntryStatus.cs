namespace BusinessApp.AccountingCore.Journals;

/// <summary>仕訳の状態（docs/04 §5）。</summary>
public enum EntryStatus
{
    /// <summary>下書き。まだ帳簿ではないので自由に編集・削除できる。</summary>
    Draft,

    /// <summary>計上済み。<b>変更も削除もできない</b>（I-05）。訂正・取消は反対仕訳で行う。</summary>
    Posted,
}
