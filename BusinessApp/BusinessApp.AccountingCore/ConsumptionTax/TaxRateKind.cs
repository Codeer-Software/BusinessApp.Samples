namespace BusinessApp.AccountingCore.ConsumptionTax;

/// <summary>
/// 税率区分（docs/11 §1・§1-1）。<b>利用者が選ぶのは「標準か軽減か」であって、その日に何 % かではない。</b>
/// </summary>
/// <remarks>
/// <para><b>率の数値はここにも税区分マスタにも持たない。</b> 制度ルールの表（<c>tax_rates</c>）が
/// 有効期間つきで持ち、この区分と日付で引く（<see cref="TaxRateBook"/>）。
/// 名前に「10%」と書くと、税率が変わった日に区分の名前が嘘になる。</para>
/// <para><b>DB の値との対応は <c>DbValue.ToSnakeCase</c>／<c>ToPascalCase</c> が付ける</b>
/// （<c>Designer/ddl/003_consumption_tax.sql</c> と <c>015_tax_rates.sql</c> の CHECK）。
/// <b>その対応を実地で見張っているのは <c>EnumConsistencyTests</c></b>（DDL・CLB・C# の 3 者突合）である。</para>
/// <para><b>旧税率 8% の区分は無い</b>——スコープの外である（docs/11 §1-1-1）。</para>
/// </remarks>
public enum TaxRateKind
{
    /// <summary>標準税率。</summary>
    Standard,

    /// <summary>軽減税率。</summary>
    Reduced,
}
