// SalesOrder.mod.cs — 受注
// 責務: 受注番号の自動採番 (SO-{yy}-{seq}) / 明細の行番号・金額(数量×単価)・合計の再計算
// 検収 (Acceptance) の生成・売上仕訳は B4-2 で実装する

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "open";
        OrderDate.Value = DateOnly.FromDateTime(DateTime.Today);
        OrderNo.Value = NextOrderNo();
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
        var l = (SalesOrderLine)row;
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

void RecalcTotal()
{
    var total = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (SalesOrderLine)row;
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    TotalAmount.Value = total;
}

// 受注番号採番: SO-{西暦下2桁}-{連番3桁}。番号の文字列降順の最大から +1 (年が変われば 1 に戻る)
string NextOrderNo()
{
    var prefix = $"SO-{DateTime.Today:yy}-";
    var s = new ModuleSearcher<SalesOrder>();
    s.OrderByDescending(e => e.OrderNo);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    var seq = 1;
    if (last != null)
    {
        var lastNo = ((SalesOrder)last).OrderNo.Value;
        if (lastNo != null && lastNo.StartsWith(prefix))
        {
            seq = int.Parse(lastNo.Substring(prefix.Length)) + 1;
        }
    }
    return $"{prefix}{seq:000}";
}
