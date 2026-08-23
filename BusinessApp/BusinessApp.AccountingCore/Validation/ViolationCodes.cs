namespace BusinessApp.AccountingCore.Validation;

/// <summary>
/// 違反の識別子。docs/04 §1 の不変条件と 1 対 1 に対応するものは番号をそのまま使う。
/// 画面はこのコードで分岐し、文言は表示にだけ使う。
/// </summary>
public static class ViolationCodes
{
    /// <summary>伝票単位で借方合計＝貸方合計。</summary>
    public const string Unbalanced = "I-01";

    /// <summary>仕訳明細は有効な会計期間に属する。</summary>
    public const string PeriodNotFound = "I-03";

    /// <summary>締め済み期間に計上・訂正・取消できない。</summary>
    public const string PeriodClosed = "I-04";

    /// <summary>訂正・取消は原仕訳を一意に特定する情報を持つ。</summary>
    public const string OriginalEntryMissing = "I-06";

    /// <summary>損益科目の仕訳明細には部門がある。</summary>
    public const string DepartmentMissing = "I-13";

    /// <summary>明細が 1 行も無い。</summary>
    public const string NoLines = "E-LINES-EMPTY";

    /// <summary>金額が正でない。金額は常に正で持ち、向きは借方貸方で表す。</summary>
    public const string AmountNotPositive = "E-AMOUNT";

    /// <summary>行番号が重複している。</summary>
    public const string DuplicateLineNo = "E-LINE-NO";

    /// <summary>勘定科目がマスタに無い。</summary>
    public const string AccountUnknown = "E-ACCOUNT-UNKNOWN";

    /// <summary>無効な勘定科目を新たに使っている。</summary>
    public const string AccountInactive = "E-ACCOUNT-INACTIVE";

    /// <summary>税区分が空。税に意味のない行にも「対象外」を明示する。</summary>
    public const string TaxCategoryMissing = "E-TAX-CATEGORY";

    /// <summary>消費税行の親行の指定が不正。</summary>
    public const string TaxLineParentInvalid = "E-TAX-PARENT";
}
