namespace BusinessApp.AccountingCore.ConsumptionTax;

/// <summary>
/// 用途区分（docs/11 §1）。個別対応方式で使う。
/// <b>勘定科目ではなく仕訳明細が持つ</b>——同じ科目でも取引ごとに変わるためである。
/// </summary>
/// <remarks>
/// 値を <c>for_...</c> にしてあるのは、課税区分（税区分マスタ）の <c>taxable_sales</c> と
/// <b>同じ値が別の意味で 2 か所に現れる</b>のを避けるためである。
/// 取り違えても値が同じだと実行時にも通ってしまう（ADR-0014 と同じ理由）。
/// </remarks>
public enum TaxTreatment
{
    /// <summary>課税売上対応。</summary>
    ForTaxableSales,

    /// <summary>共通対応。</summary>
    Common,

    /// <summary>非課税売上対応。</summary>
    ForExemptSales,
}
