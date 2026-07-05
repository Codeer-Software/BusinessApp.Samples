// Quote.mod.cs — 見積
// 責務: 見積番号の自動採番 (Q-{yy}-{seq}) / 明細の行番号・金額(数量×単価)・合計の再計算 /
//        「受注にする」= SalesOrder を生成して明細コピー (docs/08 B4-1)

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "draft";
        IssueDate.Value = DateOnly.FromDateTime(DateTime.Today);
        QuoteNo.Value = NextQuoteNo();
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
        var l = (QuoteLine)row;
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
        var l = (QuoteLine)row;
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    TotalAmount.Value = total;
}

// 見積番号採番: Q-{西暦下2桁}-{連番3桁}。番号の文字列降順の最大から +1 (年が変われば 1 に戻る)
string NextQuoteNo()
{
    var prefix = $"Q-{DateTime.Today:yy}-";
    var s = new ModuleSearcher<Quote>();
    s.OrderByDescending(e => e.QuoteNo);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    var seq = 1;
    if (last != null)
    {
        var lastNo = ((Quote)last).QuoteNo.Value;
        if (lastNo != null && lastNo.StartsWith(prefix))
        {
            seq = int.Parse(lastNo.Substring(prefix.Length)) + 1;
        }
    }
    return $"{prefix}{seq:000}";
}

// 受注番号採番: SO-{西暦下2桁}-{連番3桁} (SalesOrder 側と同一ロジック。生成はこちらでも行うため重複定義)
string NextOrderNoForConvert()
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

// 受注にする: SalesOrder を新規作成して明細をコピーし、見積を accepted に更新する
void ConvertToOrder_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("先に見積を保存してください");
        return;
    }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 既受注ガード
    var check = new ModuleSearcher<SalesOrder>();
    check.AddEquals(e => e.QuoteRef.Value, this.Id.Value);
    if (check.Execute().Count > 0)
    {
        Toaster.Error("この見積は既に受注済みです");
        return;
    }

    var orderNo = NextOrderNoForConvert();
    var so = new SalesOrder();
    so.OrderNo.Value = orderNo;
    so.QuoteRef.Value = this.Id.Value;
    so.PartnerRef.Value = PartnerRef.Value;
    so.ProjectRef.Value = ProjectRef.Value;
    so.Title.Value = Title.Value;
    so.OrderDate.Value = DateOnly.FromDateTime(DateTime.Today);
    so.Status.Value = "open";
    so.Note.Value = Note.Value;

    var count = Lines.Rows.Count;
    if (count > 0)
    {
        so.Lines.AddRows(count);
        var idx = 0;
        foreach (var row in so.Lines.Rows)
        {
            var dst = (SalesOrderLine)row;
            var src = (QuoteLine)Lines.Rows[idx];
            idx = idx + 1;
            dst.LineNo.Value = src.LineNo.Value;
            dst.Description.Value = src.Description.Value;
            dst.Qty.Value = src.Qty.Value;
            dst.UnitPrice.Value = src.UnitPrice.Value;
            dst.Amount.Value = src.Amount.Value;
            dst.TaxCategoryRef.Value = src.TaxCategoryRef.Value;
        }
    }

    var retSo = so.Submit();
    if (retSo != true)
    {
        Toaster.Error("受注の作成に失敗しました");
        return;
    }

    Status.Value = "accepted";
    var retSelf = this.Submit();
    if (retSelf != true)
    {
        Toaster.Error("見積の状態更新に失敗しました（受注は作成済みです）");
        return;
    }

    Toaster.Success($"受注 {orderNo} を作成しました");

    // 作成した受注へ遷移 (Submit 後の Id はテンポラリの可能性があるため DB から取り直す)
    var ns = new ModuleSearcher<SalesOrder>();
    ns.AddEquals(e => e.OrderNo.Value, orderNo);
    var created = ns.ExecuteFirstOrDefault();
    if (created != null)
    {
        var typedCreated = (SalesOrder)created;
        NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("SalesOrder", $"{typedCreated.Id.Value}"));
    }
}
