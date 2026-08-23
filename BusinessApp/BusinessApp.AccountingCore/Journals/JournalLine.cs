namespace BusinessApp.AccountingCore.Journals;

using BusinessApp.AccountingCore.ConsumptionTax;
using BusinessApp.AccountingCore.Shared;

/// <summary>
/// 仕訳明細（docs/04 §4-1）。金額は税抜・正の整数円で、借方貸方は <see cref="DebitCredit"/> が持つ。
/// </summary>
public sealed record JournalLine
{
    /// <summary>行番号。伝票内で一意。税行が親を指すキーでもある。</summary>
    public required int LineNo { get; init; }

    public required DebitCredit DebitCredit { get; init; }

    public required string AccountId { get; init; }

    public string? SubAccountId { get; init; }

    /// <summary>部門。損益科目では必須（I-13）。空欄を「全社共通」で穴埋めしない（docs/04 §9-1）。</summary>
    public string? DepartmentId { get; init; }

    public string? PartnerId { get; init; }

    /// <summary>
    /// 取引先名の写し。帳簿の法定記載事項①（消法 30 ⑧）であり、
    /// 取引先の改名で過去の帳簿の記載が変わらないように FK と両方持つ（docs/04 §4-2）。
    /// </summary>
    public string? PartnerNameSnapshot { get; init; }

    /// <summary>金額（税抜・正）。</summary>
    public required Yen Amount { get; init; }

    /// <summary>税区分。税に意味のない行にも「対象外」を明示する（docs/06 §1）。</summary>
    public required string TaxCategoryId { get; init; }

    /// <summary>用途区分。個別対応方式で使う。</summary>
    public TaxTreatment? TaxTreatment { get; init; }

    /// <summary>課税仕入れの時点。経過措置・税率の判定基準日（docs/06 §5）。</summary>
    public DateOnly? TaxPoint { get; init; }

    /// <summary>適用した制度ルールの版。後日マスタを更新しても過去を再計算しないための固定値（I-16）。</summary>
    public RuleVersion? AppliedRuleVersion { get; init; }

    /// <summary>消費税行か。システムが生成し、利用者は直接編集できない（docs/06 §2）。</summary>
    public bool IsTaxLine { get; init; }

    /// <summary>消費税行が対応する本体行の行番号。</summary>
    public int? ParentLineNo { get; init; }

    /// <summary>資産又は役務の内容。帳簿の法定記載事項③。</summary>
    public string? ItemDescription { get; init; }

    /// <summary>帳簿のみ保存で控除する類型（該当する場合）。</summary>
    public string? BookOnlyDeduction { get; init; }

    /// <summary>証憑を特定する情報（証憑部品への参照キー）。</summary>
    public string? EvidenceRef { get; init; }
}
