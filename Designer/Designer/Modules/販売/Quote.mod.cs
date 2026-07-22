// Quote.mod.cs — 見積
// 責務: 見積番号の自動採番 (Q-{yy}-{seq}) / 明細の行番号・金額(数量×単価)・合計の再計算 /
//        「受注にする」= SalesOrder を生成して明細コピー (docs/08 B4-1) /
//        状態遷移はボタン一元化 (ADR-0026): draft →(送付済にする)→ sent →(受注にする)→ accepted
//        draft/sent →(失注にする)→ rejected ／ sent/rejected/accepted →(下書きに戻す)→ draft
//        （accepted からは受注が存在しない場合のみ戻せる）。削除は draft のみ・詳細画面から

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
    UpdateButtons();
}

// 状態に応じたボタン出し分け (ADR-0026/0027)。青=前進・赤=巻き戻し/削除
void UpdateButtons()
{
    var st = Status.Value;
    if (this.IsNewData)
    {
        MarkSentButton.IsVisible = false;
        ConvertToOrderButton.IsVisible = false;
        MarkRejectedButton.IsVisible = false;
        RevertToDraftButton.IsVisible = false;
        DeleteQuoteButton.IsVisible = false;
        return;
    }
    MarkSentButton.IsVisible = (st == "draft");
    ConvertToOrderButton.IsVisible = (st == "draft" || st == "sent");
    MarkRejectedButton.IsVisible = (st == "draft" || st == "sent");
    RevertToDraftButton.IsVisible = (st == "sent" || st == "rejected" || st == "accepted");
    DeleteQuoteButton.IsVisible = (st == "draft");

    // 編集できるのは下書きのみ。送付済（顧客に提示済み）・失注・受注は確定文書として閲覧専用。
    // 修正したいときは「下書きに戻す」で明示的に差し戻してから編集する
    var editable = (st == "draft");
    this.IsViewOnly = !editable;
    SubmitButton.IsVisible = editable;
    if (!editable)
    {
        // CLB 1.3: モジュール全体を閲覧専用にするとボタンの OnClick も発火しなくなるため、
        // 閲覧専用中も使う操作ボタンだけ個別に閲覧専用を解除する
        ConvertToOrderButton.IsViewOnly = false;
        MarkRejectedButton.IsViewOnly = false;
        RevertToDraftButton.IsViewOnly = false;
        PrintExcelButton.IsViewOnly = false;
        PrintPdfButton.IsViewOnly = false;
    }
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
    s.OrderByDescending(e => e.QuoteNo.Value);
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
    s.OrderByDescending(e => e.OrderNo.Value);
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

// ============ 見積書の帳票出力（Excel / PDF） ============
// Resources/quote_template.xlsx（プレースホルダ差し込み方式）に1件分を転記してダウンロードする。
// 自社名・住所はテンプレート直書き（自社で編集する運用）。明細はテンプレの10行まで。
// 消費税は課税売上 10%（SALES_10）でまとめて計算（請求書と同方式）。

void PrintExcel_OnClick()
{
    PrintQuote(false);
}

void PrintPdf_OnClick()
{
    PrintQuote(true);
}

void PrintQuote(bool asPdf)
{
    if (this.IsNewData)
    {
        Toaster.Error("見積を保存してから出力してください");
        return;
    }
    using var loading = LoadingService.StartLoading(0);

    var stream = Resources.GetMemoryStream("quote_template.xlsx");
    if (stream == null)
    {
        Toaster.Error("見積書テンプレート（Resources/quote_template.xlsx）が見つかりません");
        return;
    }

    // 明細は DB から取り直す（メモリ行の遅延ロード対策）
    var ls = new ModuleSearcher<QuoteLine>();
    ls.AddEquals(e => e.QuoteId.Value, this.Id.Value);
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

    var subtotal = 0;
    foreach (var m in lines)
    {
        var l = (QuoteLine)m;
        if (l.Amount.Value != null) subtotal = subtotal + l.Amount.Value;
    }
    decimal pct = GetSalesTaxRatePercent();
    int tax = subtotal * pct / 100;
    var total = subtotal + tax;
    var issueStr = "";
    if (IssueDate.Value != null) { issueStr = $"{IssueDate.Value:yyyy年M月d日}"; }
    var validStr = "";
    if (ValidUntil.Value != null) { validStr = $"{ValidUntil.Value:yyyy年M月d日}"; }

    using (var excel = new Excel(stream, $"見積書_{QuoteNo.Value}.xlsx"))
    {
        SetByMarker(excel, "{{PARTNER}}", $"{partnerName}　御中");
        SetByMarker(excel, "{{QUOTE_NO}}", QuoteNo.Value ?? "");
        SetByMarker(excel, "{{ISSUE_DATE}}", issueStr);
        SetByMarker(excel, "{{VALID_UNTIL}}", validStr);
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
                var l = (QuoteLine)m;
                var rowCell = baseCell.GetNext(i, 0);
                excel.SetCellValue(rowCell, l.LineNo.Value ?? 0);
                excel.SetCellValue(rowCell.GetNext(0, 1), l.Description.Value ?? "");
                excel.SetCellValue(rowCell.GetNext(0, 2), l.Qty.Value ?? 0);
                excel.SetCellValue(rowCell.GetNext(0, 3), l.Unit.Value ?? "");
                excel.SetCellValue(rowCell.GetNext(0, 4), $"{l.UnitPrice.Value ?? 0:#,0}");
                excel.SetCellValue(rowCell.GetNext(0, 5), $"{l.Amount.Value ?? 0:#,0}");
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
            Toaster.Error("見積書の出力に失敗しました");
            return;
        }
    }
    if (asPdf) { Toaster.Success($"見積書 {QuoteNo.Value} を PDF でダウンロードしました"); }
    else { Toaster.Success($"見積書 {QuoteNo.Value} を Excel でダウンロードしました"); }
}

void SetByMarker(Excel excel, string marker, object value)
{
    var cell = excel.FindCellByText(marker);
    if (cell != null) { excel.SetCellValue(cell, value); }
}

// 課税売上 10% (tax_categories.code='SALES_10') の税率をマスタから解決（Invoice と同方式）
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

// 送付済にする: draft → sent
void MarkSent_OnClick()
{
    if (this.IsNewData) { Toaster.Error("先に見積を保存してください"); return; }
    if (Status.Value != "draft") { Toaster.Error("下書きの見積のみ送付済にできます"); return; }
    Status.Value = "sent";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("状態の更新に失敗しました"); return; }
    UpdateButtons();
    Toaster.Success("見積を送付済にしました");
}

// 失注にする: draft/sent → rejected
void MarkRejected_OnClick()
{
    if (this.IsNewData) { Toaster.Error("先に見積を保存してください"); return; }
    if (Status.Value != "draft" && Status.Value != "sent") { Toaster.Error("下書きまたは送付済の見積のみ失注にできます"); return; }
    Status.Value = "rejected";
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("状態の更新に失敗しました"); return; }
    UpdateButtons();
    Toaster.Success("見積を失注にしました（「下書きに戻す」で復活できます）");
}

// 下書きに戻す: sent/rejected → draft。accepted は受注が存在しない場合のみ（誤操作の巻き戻し）
void RevertToDraft_OnClick()
{
    if (Status.Value == "accepted")
    {
        var check = new ModuleSearcher<SalesOrder>();
        check.AddEquals(e => e.QuoteRef.Value, this.Id.Value);
        var found = check.ExecuteFirstOrDefault();
        if (found != null)
        {
            var orderNo = ((SalesOrder)found).OrderNo.Value;
            Toaster.Error($"受注 {orderNo} が存在するため下書きに戻せません（先に受注側を削除してください）");
            return;
        }
    }
    Status.Value = "draft";
    this.IsViewOnly = false;
    SubmitButton.IsVisible = true;
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("状態の更新に失敗しました"); return; }
    UpdateButtons();
    Toaster.Success("見積を下書きに戻しました");
}

// 見積の削除: 下書きのみ（確認ダイアログ付き）
void DeleteQuote_OnClick()
{
    if (Status.Value != "draft") { Toaster.Error("下書きの見積のみ削除できます"); return; }
    var result = MessageBox.Show($"見積「{QuoteNo.Value} {Title.Value}」を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;
    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("見積を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("Quote"));
}

// 受注にする: SalesOrder を新規作成して明細をコピーし、見積を accepted に更新する
void ConvertToOrder_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("先に見積を保存してください");
        return;
    }
    if (Status.Value == "rejected")
    {
        Toaster.Error("失注した見積からは受注を作成できません（「下書きに戻す」で復活してから操作してください）");
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
            dst.Unit.Value = src.Unit.Value;
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
