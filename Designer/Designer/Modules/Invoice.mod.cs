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

// ============ 請求書の帳票出力（Excel / PDF） ============
// Resources/invoice_template.xlsx（プレースホルダ差し込み方式）に1件分を転記してダウンロードする。
// 自社名・住所・振込先はテンプレート直書き（自社で編集する運用）。明細はテンプレの10行まで。

void PrintExcel_OnClick()
{
    PrintInvoice(false);
}

void PrintPdf_OnClick()
{
    PrintInvoice(true);
}

void PrintInvoice(bool asPdf)
{
    if (this.IsNewData)
    {
        Toaster.Error("請求書を保存してから出力してください");
        return;
    }
    using var loading = LoadingService.StartLoading(0);

    var stream = Resources.GetMemoryStream("invoice_template.xlsx");
    if (stream == null)
    {
        Toaster.Error("請求書テンプレート（Resources/invoice_template.xlsx）が見つかりません");
        return;
    }

    // 明細は DB から取り直す（メモリ行の遅延ロード対策）
    var ls = new ModuleSearcher<InvoiceLine>();
    ls.AddEquals(e => e.InvoiceId.Value, this.Id.Value);
    ls.OrderBy(e => e.LineNo.Value);
    var lines = ls.Execute();

    // 取引先名
    var partnerName = "";
    if (PartnerRef.Value != null)
    {
        var pc = new ModuleSearcher<Partner>();
        pc.AddEquals(p => p.Id.Value, PartnerRef.Value);
        var pt = pc.ExecuteFirstOrDefault();
        if (pt != null) { partnerName = ((Partner)pt).Name.Value ?? ""; }
    }

    var subtotal = Amount.Value ?? 0;
    var tax = TaxAmount.Value ?? 0;
    var total = subtotal + tax;
    var issueStr = "";
    if (IssueDate.Value != null) { issueStr = $"{IssueDate.Value:yyyy年M月d日}"; }
    var dueStr = "";
    if (DueDate.Value != null) { dueStr = $"{DueDate.Value:yyyy年M月d日}"; }

    using (var excel = new Excel(stream, $"請求書_{InvoiceNo.Value}.xlsx"))
    {
        SetByMarker(excel, "{{PARTNER}}", $"{partnerName}　御中");
        SetByMarker(excel, "{{INVOICE_NO}}", InvoiceNo.Value ?? "");
        SetByMarker(excel, "{{ISSUE_DATE}}", issueStr);
        SetByMarker(excel, "{{DUE_DATE}}", dueStr);
        SetByMarker(excel, "{{TITLE}}", Title.Value ?? "");
        SetByMarker(excel, "{{TOTAL}}", $"￥{total:#,0} -");
        SetByMarker(excel, "{{SUBTOTAL}}", $"{subtotal:#,0}");
        SetByMarker(excel, "{{TAX}}", $"{tax:#,0}");
        SetByMarker(excel, "{{TOTAL2}}", $"{total:#,0}");
        SetByMarker(excel, "{{NOTE}}", Note.Value ?? "");

        var baseCell = excel.FindCellByText("{{LINES}}");
        if (baseCell != null)
        {
            excel.SetCellValue(baseCell, "");
            var i = 0;
            foreach (var m in lines)
            {
                if (i >= 10) break;  // テンプレートの明細枠は10行
                var l = (InvoiceLine)m;
                var rowCell = baseCell.GetNext(i, 0);
                excel.SetCellValue(rowCell, l.LineNo.Value ?? 0);
                excel.SetCellValue(rowCell.GetNext(0, 1), l.Description.Value ?? "");
                excel.SetCellValue(rowCell.GetNext(0, 2), l.Qty.Value ?? 0);
                excel.SetCellValue(rowCell.GetNext(0, 3), $"{l.UnitPrice.Value ?? 0:#,0}");
                excel.SetCellValue(rowCell.GetNext(0, 4), $"{l.Amount.Value ?? 0:#,0}");
                i = i + 1;
            }
            if (lines.Count > 10)
            {
                Toaster.Warn($"明細が10行を超えています（{lines.Count}行）。11行目以降は出力されません");
            }
        }

        var ok = false;
        if (asPdf) { ok = excel.DownloadPdf(); }
        else { ok = excel.Download(); }
        if (!ok)
        {
            Toaster.Error("請求書の出力に失敗しました");
            return;
        }
    }
    if (asPdf) { Toaster.Success($"請求書 {InvoiceNo.Value} を PDF でダウンロードしました"); }
    else { Toaster.Success($"請求書 {InvoiceNo.Value} を Excel でダウンロードしました"); }
}

void SetByMarker(Excel excel, string marker, object value)
{
    var cell = excel.FindCellByText(marker);
    if (cell != null) { excel.SetCellValue(cell, value); }
}
