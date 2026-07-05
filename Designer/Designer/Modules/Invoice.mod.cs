// Invoice.mod.cs — 請求書
// 責務: 請求書番号採番 (INV-{yy}-{seq}) / 明細の行番号・金額・合計の再計算と
//        請求額(税抜)・消費税額(SALES_10 税率)の自動反映 / 支払期限の既定=翌月末
// 入金消込・売掛管理は B4-4 で実装する

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "issued";
        if (InvoiceSource.Value == null || InvoiceSource.Value == "") { InvoiceSource.Value = "manual"; }
        IssueDate.Value = DateOnly.FromDateTime(DateTime.Today);
        DueDate.Value = EndOfNextMonth();
        InvoiceNo.Value = NextInvoiceNo();
    }
    RecalcTotal();
}

void Lines_OnDataChanged()
{
    if (inLinesHandler) return;
    inLinesHandler = true;
    var no = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (InvoiceLine)row;
        no = no + 1;
        l.LineNo.Value = no;
        if (l.Qty.Value == null) l.Qty.Value = 1;
        if (l.UnitPrice.Value != null)
        {
            l.Amount.Value = l.Qty.Value * l.UnitPrice.Value;
        }
    }
    RecalcTotal();
    inLinesHandler = false;
}

// 明細合計 → 表示用合計・請求額(税抜)・消費税額 (SALES_10 税率で切り捨て) を更新
void RecalcTotal()
{
    var total = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (InvoiceLine)row;
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    TotalAmount.Value = total;
    Amount.Value = total;
    decimal pct = GetSalesTaxRatePercent();
    int tax = total * pct / 100;
    TaxAmount.Value = tax;
}

// 課税売上 10% (tax_categories.code='SALES_10') の税率をマスタから解決
decimal GetSalesTaxRatePercent()
{
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Code.Value, "SALES_10");
    var found = cs.ExecuteFirstOrDefault();
    if (found == null) return 0;
    var tcat = (TaxCategory)found;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    return ((TaxRate)foundRate).RatePercent.Value ?? 0;
}

// 請求書番号採番: INV-{西暦下2桁}-{連番3桁}
string NextInvoiceNo()
{
    var prefix = $"INV-{DateTime.Today:yy}-";
    var s = new ModuleSearcher<Invoice>();
    s.OrderByDescending(e => e.InvoiceNo.Value);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    var seq = 1;
    if (last != null)
    {
        var lastNo = ((Invoice)last).InvoiceNo.Value;
        if (lastNo != null && lastNo.StartsWith(prefix))
        {
            seq = int.Parse(lastNo.Substring(prefix.Length)) + 1;
        }
    }
    return $"{prefix}{seq:000}";
}

// 翌月末日 (支払サイト: 月末締め翌月末払いの既定)
DateOnly EndOfNextMonth()
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
    var firstOfMonthAfterNext = firstOfThisMonth.AddMonths(2);
    return firstOfMonthAfterNext.AddDays(-1);
}
