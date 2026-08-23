namespace BusinessApp.AccountingCore.ConsumptionTax;

/// <summary>
/// 用途区分（docs/06 §1）。個別対応方式で使う。
/// <b>勘定科目ではなく仕訳明細が持つ</b>——同じ科目でも取引ごとに変わるためである。
/// </summary>
public enum TaxTreatment
{
    /// <summary>課税売上対応。</summary>
    TaxableSales,

    /// <summary>共通対応。</summary>
    Common,

    /// <summary>非課税売上対応。</summary>
    ExemptSales,
}
