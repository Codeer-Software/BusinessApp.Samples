// Invoice.mod.cs — 請求書
// 責務: 請求書番号採番 (INV-{yy}-{seq}) / 明細の行番号・金額・合計の再計算と
//        請求額(税抜)・消費税額(SALES_10 税率)の自動反映 / 支払期限の既定=翌月末
// 入金消込・売掛管理は B4-4 で実装する

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        // 手作成の請求書は「下書き」始まり（U4-5・2026-07-16 ユーザー決定。見積と対称にする）。
        // 検収・定期請求・SES からの自動生成は各スクリプトが直接 issued を書くため影響なし。
        Status.Value = "draft";
        if (InvoiceSource.Value == null || InvoiceSource.Value == "") { InvoiceSource.Value = "manual"; }
        IssueDate.Value = DateOnly.FromDateTime(DateTime.Today);
        DueDate.Value = EndOfNextMonth();
        InvoiceNo.Value = NextInvoiceNo();
        // 部門の初期値: 作成者の所属部門（検収・定期・SES からの自動生成は各スクリプトが設定）
        if (DepartmentRef.Value == null) { DepartmentRef.Value = CurrentUser.所属部門.Value; }
    }
    RecalcTotal();
    UpdateButtons();
}

// 状態遷移はボタン経由に一本化（ADR-0026）。状態セレクトは表示専用。
// draft: 発行する・削除 ／ issued: 下書きに戻す・取消にする ／ void: 取消を戻す ／
// partial・paid: 遷移ボタンなし（入金側で自動遷移）
void UpdateButtons()
{
    // 部門は経理のみ変更可（2026-07-25 ユーザー要望）。一般・承認者は自部門（初期値）固定
    // （請求書自体は経理専用モジュールだが、権限方針を見積・受注と揃えて明示しておく）
    var isAccounting = (CurrentUser.Role.Value == "accounting" || CurrentUser.Role.Value == "sysadmin");
    if (!isAccounting) { DepartmentRef.IsViewOnly = true; }

    var st = Status.Value;
    IssueButton.IsVisible = !this.IsNewData && (st == "draft");
    DeleteInvoiceButton.IsVisible = !this.IsNewData && (st == "draft");
    RevertToDraftButton.IsVisible = !this.IsNewData && (st == "issued");
    VoidButton.IsVisible = !this.IsNewData && (st == "issued");
    UnvoidButton.IsVisible = !this.IsNewData && (st == "void");

    // 編集できるのは下書きのみ（2026-07-25 ユーザー要望。見積と同じ確定文書ロック）。
    // 発行済・一部入金・入金済・取消は閲覧専用——修正は「下書きに戻す」で明示的に差し戻してから行う
    var editable = this.IsNewData || (st == "draft");
    this.IsViewOnly = !editable;
    SubmitButton.IsVisible = editable;
    if (!editable)
    {
        // CLB 1.3: モジュール全体を閲覧専用にするとボタンの OnClick も発火しなくなるため、
        // 閲覧専用中も使う操作ボタンだけ個別に閲覧専用を解除する
        RevertToDraftButton.IsViewOnly = false;
        VoidButton.IsViewOnly = false;
        UnvoidButton.IsViewOnly = false;
        PrintExcelButton.IsViewOnly = false;
        PrintPdfButton.IsViewOnly = false;
    }
}

// 請求書の取消（issued→void）: 貸倒れ・二重発行などで請求を無効化し、売掛残高の対象から外す。
// 入金記録がある請求書は不可（先に入金の取消を）。売上仕訳は消さない——貸倒れは貸倒損失の
// 振替伝票（07_特殊取引の手順）で別途処理する
void Void_OnClick()
{
    if (Status.Value != "issued") { Toaster.Error("発行済の請求書のみ取消にできます"); return; }
    if (HasConfirmedReceipts()) { Toaster.Error("消込済みの入金記録があるため取消にできません（先に入金の取消を行ってください）"); return; }
    var result = MessageBox.Show($"請求書「{InvoiceNo.Value}」を取消にしますか？（入金消込・売掛残高の対象から外れます。計上済みの売上仕訳はそのまま残るため、貸倒れ等は別途振替伝票で処理してください）", "取消にする", "キャンセル");
    if (result != "取消にする") return;

    using var loading = LoadingService.StartLoading(0);
    this.IsViewOnly = false;  // 発行済ロック中でも状態遷移は許可（UpdateButtons が再ロックする）
    Status.Value = "void";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "issued";
        Toaster.Error("取消に失敗しました");
        UpdateButtons();
        return;
    }
    DeletePendingReceipts();  // 未確定の入金予定は取消と同時に片付ける（消込対象から外す）
    Toaster.Success($"請求書 {InvoiceNo.Value} を取消にしました");
    UpdateButtons();
}

// 取消の取り消し（void→issued）: 誤って取消にした場合のリカバリ
void Unvoid_OnClick()
{
    if (Status.Value != "void") { Toaster.Error("取消状態の請求書のみ戻せます"); return; }
    using var loading = LoadingService.StartLoading(0);
    this.IsViewOnly = false;  // 取消ロック中でも状態遷移は許可（UpdateButtons が再ロックする）
    Status.Value = "issued";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "void";
        Toaster.Error("発行済への変更に失敗しました");
        UpdateButtons();
        return;
    }
    CreatePendingReceipt();  // 取消の取り消しで消込対象に復帰するため、入金予定も作り直す
    Toaster.Success($"請求書 {InvoiceNo.Value} を発行済に戻しました");
    UpdateButtons();
}

void Issue_OnClick()
{
    if (Status.Value != "draft") { Toaster.Error("下書きの請求書のみ発行できます"); return; }
    var hasLine = false;
    foreach (var row in Lines.Rows) { hasLine = true; break; }
    if (!hasLine) { Toaster.Error("明細を入力してから発行してください"); return; }

    using var loading = LoadingService.StartLoading(0);
    Status.Value = "issued";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "draft";
        Toaster.Error("発行に失敗しました");
        return;
    }
    CreatePendingReceipt();
    Toaster.Success($"請求書 {InvoiceNo.Value} を発行しました（入金予定を自動作成。入金一覧の「未確定」から消込できます）");
    UpdateButtons();
}

// この請求書に入金記録があるか（下書き削除のガード。未確定の入金予定も孤児にしないため含める）
bool HasReceipts()
{
    var rs = new ModuleSearcher<Receipt>();
    rs.AddEquals(e => e.InvoiceRef.Value, this.Id.Value);
    return rs.Execute().Count > 0;
}

// 消込済み（消込仕訳が存在する）入金があるか（取消・巻き戻しのガード。
// 発行時に自動作成される未確定の入金予定はブロックしない——それは DeletePendingReceipts で片付ける）
bool HasConfirmedReceipts()
{
    var rs = new ModuleSearcher<Receipt>();
    rs.AddEquals(e => e.InvoiceRef.Value, this.Id.Value);
    var rows = rs.Execute();
    foreach (var row in rows)
    {
        var r = (Receipt)row;
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, r.Id.Value);
        if (js.Execute().Count > 0) { return true; }
    }
    return false;
}

// 入金予定（未確定入金）の自動作成（2026-07-25 ユーザー要望）。
// 発行と同時に入金一覧へ「未確定」の行ができ、それがそのまま経理の消込 ToDo になる。
// 入金日は支払期限を予定日として仮置き・金額は税込請求額——確定時に経理が実額へ修正する
void CreatePendingReceipt()
{
    var rs = new ModuleSearcher<Receipt>();
    rs.AddEquals(e => e.InvoiceRef.Value, this.Id.Value);
    if (rs.Execute().Count > 0) { return; }  // 既に入金記録がある請求書には作らない（二重作成ガード）
    var r = new Receipt();
    r.InvoiceRef.Value = this.Id.Value;
    r.ReceiptDate.Value = DueDate.Value;
    r.Method.Value = "bank";
    r.Amount.Value = (Amount.Value ?? 0) + (TaxAmount.Value ?? 0);
    r.Note.Value = "請求書の発行時に自動作成された入金予定です（入金日・金額を実額に修正して確定してください）";
    var ok = r.Submit();
    if (ok != true) { Toaster.Warn("入金予定の自動作成に失敗しました（入金画面から手動で登録してください）"); }
}

// 未確定（消込仕訳なし）の入金予定を削除する（下書きへの巻き戻し・取消時の後始末）
void DeletePendingReceipts()
{
    var rs = new ModuleSearcher<Receipt>();
    rs.AddEquals(e => e.InvoiceRef.Value, this.Id.Value);
    var rows = rs.Execute();
    foreach (var row in rows)
    {
        var r = (Receipt)row;
        var js = new ModuleSearcher<JournalEntry>();
        js.AddEquals(e => e.SourceType.Value, "receipt");
        js.AddEquals(e => e.SourceId.Value, r.Id.Value);
        if (js.Execute().Count > 0) { continue; }
        var ok = r.Delete();
        if (ok != true) { Toaster.Warn("未確定の入金予定の削除に失敗しました（入金一覧から手動で削除してください）"); }
    }
}

// 定期請求・SES など「生成と同時に売上仕訳が起票される」請求書か
// （journal_entries.source_id に請求書 id が入る source_type 群）
bool HasGenerationJournal()
{
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var rows = js.Execute();
    foreach (var row in rows)
    {
        var je = (JournalEntry)row;
        var st = je.SourceType.Value;
        if (st == "ses" || st == "recurring" || st == "recurring_annual" || st == "recurring_defer")
        {
            return true;
        }
    }
    return false;
}

// 発行の巻き戻し（issued→draft）。入金・売上仕訳が絡む場合は不可
void RevertToDraft_OnClick()
{
    if (Status.Value == "partial" || Status.Value == "paid")
    {
        Toaster.Error("入金記録があるため下書きに戻せません（先に入金の取消を行ってください）");
        return;
    }
    if (Status.Value != "issued") { Toaster.Error("発行済の請求書のみ下書きに戻せます"); return; }
    if (HasConfirmedReceipts())
    {
        Toaster.Error("消込済みの入金記録があるため下書きに戻せません（先に入金の取消を行ってください）");
        return;
    }
    if (HasGenerationJournal())
    {
        Toaster.Error("この請求書は発行時に売上仕訳が起票されています（定期請求/SES）。修正は赤黒訂正で行ってください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);
    this.IsViewOnly = false;  // 発行済ロック中でも状態遷移は許可（UpdateButtons が再ロックする）
    Status.Value = "draft";
    var ret = this.Submit();
    if (ret != true)
    {
        Status.Value = "issued";
        Toaster.Error("下書きへの変更に失敗しました");
        UpdateButtons();
        return;
    }
    DeletePendingReceipts();  // 発行時に自動作成した入金予定は巻き戻しと同時に片付ける
    Toaster.Success("請求書を下書きに戻しました");
    UpdateButtons();
}

// 下書きの削除（ADR-0026: 削除は詳細画面の条件付きボタンのみ・一覧の削除ボタンは撤去）
void DeleteInvoice_OnClick()
{
    if (Status.Value != "draft") { Toaster.Error("下書きの請求書のみ削除できます"); return; }
    if (HasReceipts()) { Toaster.Error("入金記録があるため削除できません"); return; }
    if (HasGenerationJournal()) { Toaster.Error("売上仕訳が起票されているため削除できません"); return; }
    var result = MessageBox.Show($"請求書「{InvoiceNo.Value}」を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;

    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("請求書を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("Invoice"));
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

    // ファイル名: 請求書_{発行日}_{請求書番号}_{相手方}_{件名}（2026-07-25 ユーザー要望。見積書と同形式）
    var issueForFile = "";
    if (IssueDate.Value != null) { issueForFile = $"{IssueDate.Value:yyyyMMdd}"; }
    var fileName = SanitizeFileName($"請求書_{issueForFile}_{InvoiceNo.Value}_{partnerName}_{Title.Value}") + ".xlsx";

    using (var excel = new Excel(stream, fileName))
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

// Windows で使えないファイル名文字を「-」に置換（取引先名・件名由来の事故防止）
string SanitizeFileName(string name)
{
    var s = name ?? "";
    s = s.Replace("\\", "-").Replace("/", "-").Replace(":", "-").Replace("*", "-").Replace("?", "-").Replace("\"", "-").Replace("<", "-").Replace(">", "-").Replace("|", "-");
    return s;
}
