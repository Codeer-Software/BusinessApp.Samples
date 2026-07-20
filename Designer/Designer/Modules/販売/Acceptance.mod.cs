// Acceptance.mod.cs — 検収
// 責務: 検収番号採番 (A-{yy}-{seq}) / 受注選択時の検収額・消費税の自動セット /
//        検収確定→売上仕訳 (D 売掛金 / C 売上高+仮受消費税。検収基準 = decisions/0008、経理ロール専用) /
//        確定後の請求書作成 (B4-3)
// 仕訳生成の正典: ExpenseRequest.GenerateJournal_OnClick (ガード・採番・税行の同型)

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "draft";
        AcceptanceDate.Value = DateOnly.FromDateTime(DateTime.Today);
        AcceptanceNo.Value = NextAcceptanceNo();
    }
    UpdateButtons();
    UpdateOrderProgress();
}

// 受注額・検収済み累計（確定）・残額の表示（P2: 分割検収・変更契約時の二重計上防止）
void UpdateOrderProgress()
{
    if (SalesOrderRef.Value == null)
    {
        OrderProgressLabel.IsVisible = false;
        return;
    }
    var orderTotal = GetOrderTotal(SalesOrderRef.Value);
    var confirmedTotal = GetConfirmedTotal(SalesOrderRef.Value);
    OrderProgressLabel.Text = $"受注額 {orderTotal:#,0} 円 ／ 検収済み累計（確定） {confirmedTotal:#,0} 円 ／ 残額 {orderTotal - confirmedTotal:#,0} 円";
    OrderProgressLabel.IsVisible = true;
}

// 受注明細の税抜合計
int GetOrderTotal(object salesOrderId)
{
    var ls = new ModuleSearcher<SalesOrderLine>();
    ls.AddEquals(l => l.SalesOrderId.Value, salesOrderId);
    var lines = ls.Execute();
    var total = 0;
    foreach (var row in lines)
    {
        var l = (SalesOrderLine)row;
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    return total;
}

// この受注の確定済み検収額の合計
int GetConfirmedTotal(object salesOrderId)
{
    var s = new ModuleSearcher<Acceptance>();
    s.AddEquals(e => e.SalesOrderRef.Value, salesOrderId);
    s.AddEquals(e => e.Status.Value, "confirmed");
    var rows = s.Execute();
    var total = 0;
    foreach (var m in rows)
    {
        var a = (Acceptance)m;
        if (a.Amount.Value != null) total = total + a.Amount.Value;
    }
    return total;
}

void UpdateButtons()
{
    var st = Status.Value;
    if (!this.IsNewData && st == "confirmed")
    {
        this.IsViewOnly = true;
        // CLB 1.3: モジュール全体を閲覧専用にするとボタンの OnClick も発火しなくなるため、
        // 確定後も操作する取消・請求書作成ボタン・合算先セレクトだけ個別に閲覧専用を解除する
        CancelConfirmButton.IsViewOnly = false;
        CreateInvoiceButton.IsViewOnly = false;
        BilledInvoiceRef.IsViewOnly = false;
    }
    else
    {
        this.IsViewOnly = false;
    }
    // 確定（売上計上）・請求書作成・確定取消は経理の業務。営業には最初からボタンを見せない
    // （役割分担: 営業=検収事実の記録（下書き）、経理=会計処理の確定）
    var isAccountingRole = (CurrentUser.Role.Value == "accounting" || CurrentUser.Role.Value == "sysadmin");
    ConfirmButton.IsVisible = isAccountingRole && !this.IsNewData && (st == "draft");

    // 確定後は編集できないので保存ボタンは出さない（押せないボタンを見せない・ADR-0027）
    SubmitButton.IsVisible = !(st == "confirmed" && !this.IsNewData);

    // 請求書作成済み（直接 or 合算）ならボタンの代わりに案内ラベルを出す
    var invoiceNo = FindExistingInvoiceNo();
    var billedNo = FindBilledInvoiceNo();
    CreateInvoiceButton.IsVisible = isAccountingRole && !this.IsNewData && (st == "confirmed") && (invoiceNo == null) && (billedNo == null);
    InvoiceDoneLabel.IsVisible = (invoiceNo != null || billedNo != null);
    if (invoiceNo != null)
    {
        InvoiceDoneLabel.Text = $"請求書 {invoiceNo} 作成済み（販売管理＞請求書から確認できます）";
    }
    else if (billedNo != null)
    {
        InvoiceDoneLabel.Text = $"請求書 {billedNo} に合算済み（販売管理＞請求書から確認できます）";
    }

    // 合算先請求書の選択欄: 経理のみ・確定済み・直接請求書が無いときだけ出す
    var showBilled = isAccountingRole && !this.IsNewData && (st == "confirmed") && (invoiceNo == null);
    BilledInvoiceLabel.IsVisible = showBilled;
    BilledInvoiceRef.IsVisible = showBilled;
    BilledInvoiceHint.IsVisible = showBilled && (billedNo == null);

    CancelConfirmButton.IsVisible = isAccountingRole && !this.IsNewData && (st == "confirmed");
    DeleteAcceptanceButton.IsVisible = !this.IsNewData && (st == "draft");
}

// 仕訳を明細→親の順に物理削除する。子持ちモジュールの検索インスタンス Delete() は
// 親単独では静かに失敗する（実測）ため、行ごとに削除し全戻り値を検証する
bool DeleteJournalEntryWithLines(JournalEntry je)
{
    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(l => l.JournalEntryId.Value, je.Id.Value);
    var lines = ls.Execute();
    foreach (var row in lines)
    {
        var l = (JournalLine)row;
        var okLine = l.Delete();
        if (okLine != true) { return false; }
    }
    var ok = je.Delete();
    if (ok != true) { return false; }
    return true;
}

// この検収から作成済みの請求書番号（無ければ null）
string FindExistingInvoiceNo()
{
    if (this.IsNewData) return null;
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.AcceptanceRef.Value, this.Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return ((Invoice)found).InvoiceNo.Value;
}

// 確定の取り消し (confirmed → draft): 売上仕訳を削除して下書きに戻す（経理ロール専用）
// 請求書が既にある場合・仕訳の期間が締め済みの場合は不可（先にそちらを解消する）
void CancelConfirm_OnClick()
{
    if (CurrentUser.Role.Value != "accounting" && CurrentUser.Role.Value != "sysadmin")
    {
        Toaster.Error("確定の取り消しは経理ロールのみ実行できます");
        return;
    }
    if (Status.Value != "confirmed") { Toaster.Error("確定済みの検収のみ取り消せます"); return; }
    var invoiceNo = FindExistingInvoiceNo();
    if (invoiceNo != null)
    {
        Toaster.Error($"請求書 {invoiceNo} が作成済みのため取り消せません（先に請求書側を削除してください）");
        return;
    }

    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "acceptance");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var found = js.ExecuteFirstOrDefault();
    if (found != null)
    {
        var je = (JournalEntry)found;
        // 仕訳日の期間が締め済みなら削除しない（帳簿の整合性優先。決算修正仕訳へ誘導）
        var d = je.EntryDate.Value;
        if (d != null)
        {
            var monthFirst = new DateOnly(d.Year, d.Month, 1);
            var ps = new ModuleSearcher<FiscalPeriod>();
            ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
            ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
            var period = ps.ExecuteFirstOrDefault();
            if (period != null && ((FiscalPeriod)period).Status.Value == "closed")
            {
                Toaster.Error("売上仕訳の期間が締め済みのため取り消せません（決算修正仕訳（赤伝）で対応してください）");
                return;
            }
        }
        var jeNo = je.JournalNo.Value;
        var result = MessageBox.Show($"売上仕訳 No.{jeNo} を削除して検収を下書きに戻します。よろしいですか？", "取り消す", "キャンセル");
        if (result != "取り消す") return;
        using var loading = LoadingService.StartLoading(0);
        if (!DeleteJournalEntryWithLines(je))
        {
            Toaster.Error("売上仕訳の削除に失敗しました（検収は確定済みのままです）");
            return;
        }
    }
    else
    {
        var result = MessageBox.Show("検収を下書きに戻します。よろしいですか？（対応する売上仕訳は見つかりませんでした）", "取り消す", "キャンセル");
        if (result != "取り消す") return;
    }

    this.IsViewOnly = false;
    Status.Value = "draft";
    var ret = this.Submit();
    if (ret != true) { Toaster.Error("検収ステータスの更新に失敗しました"); return; }
    UpdateButtons();
    UpdateOrderProgress();
    Toaster.Success("検収の確定を取り消し、下書きに戻しました");
}

// 下書き検収の削除（確認ダイアログ付き）
void DeleteAcceptance_OnClick()
{
    if (Status.Value != "draft") { Toaster.Error("下書きの検収のみ削除できます"); return; }
    var result = MessageBox.Show($"検収「{AcceptanceNo.Value}」を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;
    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("検収を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("Acceptance"));
}

// 受注選択: 受注の未検収残額（受注額−確定済み検収累計）を検収額に、SALES_10 税率で消費税を自動セット (手修正可)
// 初回検収では残額＝受注全額。分割検収の2回目以降は残額がセットされ、うっかり全額の再計上を防ぐ（P2）
void SalesOrderRef_OnDataChanged()
{
    if (SalesOrderRef.Value == null)
    {
        UpdateOrderProgress();
        return;
    }
    var orderTotal = GetOrderTotal(SalesOrderRef.Value);
    var confirmedTotal = GetConfirmedTotal(SalesOrderRef.Value);
    var remaining = orderTotal - confirmedTotal;
    if (remaining < 0) remaining = 0;
    Amount.Value = remaining;
    decimal pct = GetSalesTaxRatePercent();
    int tax = remaining * pct / 100;
    TaxAmount.Value = tax;
    UpdateOrderProgress();
}

// この検収の合算先請求書の番号（未設定なら null）
string FindBilledInvoiceNo()
{
    if (BilledInvoiceRef.Value == null) return null;
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.Id.Value, BilledInvoiceRef.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return ((Invoice)found).InvoiceNo.Value;
}

// 合算先請求書の選択・解除は即保存する（確定済み検収は閲覧専用で保存ボタンが無いため）
void BilledInvoiceRef_OnDataChanged()
{
    if (this.IsNewData) return;
    if (Status.Value != "confirmed") return;
    using var loading = LoadingService.StartLoading(0);
    this.IsViewOnly = false;
    var ret = this.Submit();
    if (ret == false)
    {
        Toaster.Error("合算先請求書の保存に失敗しました");
        UpdateButtons();
        return;
    }
    UpdateButtons();
    if (BilledInvoiceRef.Value != null)
    {
        Toaster.Success("合算先請求書を記録しました（この検収からの請求書作成はできなくなります）");
    }
    else
    {
        Toaster.Info("合算先請求書を解除しました（「請求書を作成」が再び使えます）");
    }
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

// 検収番号採番: A-{西暦下2桁}-{連番3桁}
string NextAcceptanceNo()
{
    var prefix = $"A-{DateTime.Today:yy}-";
    var s = new ModuleSearcher<Acceptance>();
    s.OrderByDescending(e => e.AcceptanceNo.Value);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    var seq = 1;
    if (last != null)
    {
        var lastNo = ((Acceptance)last).AcceptanceNo.Value;
        if (lastNo != null && lastNo.StartsWith(prefix))
        {
            seq = int.Parse(lastNo.Substring(prefix.Length)) + 1;
        }
    }
    return $"{prefix}{seq:000}";
}

// 検収確定: 売上仕訳を生成して confirmed へ (経理ロール専用)
// D 売掛金1100 (税込) / C 売上科目 (税抜, 案件区分で 4000/4010/4020) / C 仮受消費税2200 (税行)
void Confirm_OnClick()
{
    if (CurrentUser.Role.Value != "accounting" && CurrentUser.Role.Value != "sysadmin")
    {
        Toaster.Error("検収の確定（売上計上）は経理ロールのみ実行できます");
        return;
    }
    if (this.IsNewData) { Toaster.Error("先に検収を保存してください"); return; }
    if (Status.Value == "confirmed") { Toaster.Error("この検収は確定済みです"); return; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("検収額を入力してください"); return; }
    if (AcceptanceDate.Value == null) { Toaster.Error("検収日を入力してください"); return; }
    if (SalesOrderRef.Value == null) { Toaster.Error("受注を選択してください"); return; }

    // 受注額超過の警告（P2: 分割検収・変更契約時の二重計上防止）。
    // 増額検収は実務上ありうるため、ブロックはせず確認だけ求める
    var warnOrderTotal = GetOrderTotal(SalesOrderRef.Value);
    var warnConfirmedTotal = GetConfirmedTotal(SalesOrderRef.Value);
    if (warnOrderTotal > 0 && warnConfirmedTotal + Amount.Value > warnOrderTotal)
    {
        var over = warnConfirmedTotal + Amount.Value - warnOrderTotal;
        var answer = MessageBox.Show($"確定済みの検収累計 {warnConfirmedTotal:#,0} 円にこの検収 {Amount.Value:#,0} 円を加えると、受注額 {warnOrderTotal:#,0} 円を {over:#,0} 円超過します。このまま確定しますか？", "確定する", "キャンセル");
        if (answer != "確定する") return;
    }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "acceptance");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) { Toaster.Error("この検収の売上仕訳は既に生成済みです"); return; }

    // 会計年度の解決と締め済み期間ガード (境界日知見: 月末日は辞書順比較で失敗するため月初日で解決)
    var accMonthFirst = new DateOnly(AcceptanceDate.Value.Year, AcceptanceDate.Value.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, accMonthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, accMonthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("検収日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, accMonthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, accMonthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("検収日に対応する月次期間がありません"); return; }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("検収日の期間は締め済みです。仕訳は手動で起票してください"); return; }

    // 受注と案件区分から売上科目コードを解決 (contract=4000 / ses=4010 / saas=4020、未設定は 4000)
    var os = new ModuleSearcher<SalesOrder>();
    os.AddEquals(e => e.Id.Value, SalesOrderRef.Value);
    var so = os.ExecuteFirstOrDefault();
    if (so == null) { Toaster.Error("受注が見つかりません"); return; }
    var typedSo = (SalesOrder)so;

    var salesCode = "4000";
    if (typedSo.ProjectRef.Value != null)
    {
        var prs = new ModuleSearcher<Project>();
        prs.AddEquals(p => p.Id.Value, typedSo.ProjectRef.Value);
        var proj = prs.ExecuteFirstOrDefault();
        if (proj != null)
        {
            var ptype = ((Project)proj).ProjectType.Value;
            if (ptype == "ses") { salesCode = "4010"; }
            if (ptype == "saas") { salesCode = "4020"; }
        }
    }

    // 科目解決: 売掛金1100 / 売上科目 / 仮受消費税2200
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1100", "2200", salesCode);
    var accounts = accS.Execute();
    object arAccountId = null;
    object salesAccountId = null;
    object taxAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1100") { arAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2200") { taxAccountId = acc.Id.Value; }
        if (acc.Code.Value == salesCode) { salesAccountId = acc.Id.Value; }
    }
    if (arAccountId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }
    if (salesAccountId == null) { Toaster.Error($"売上科目({salesCode})がありません"); return; }
    if (taxAccountId == null) { Toaster.Error("仮受消費税(2200)の科目がありません"); return; }

    // 税区分 (SALES_10) の id
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Code.Value, "SALES_10");
    var tcat = cs.ExecuteFirstOrDefault();
    object salesTaxCatId = null;
    if (tcat != null) { salesTaxCatId = ((TaxCategory)tcat).Id.Value; }

    int amount = Amount.Value;
    int tax = TaxAmount.Value ?? 0;
    int gross = amount + tax;

    // 伝票採番
    var ns = new ModuleSearcher<JournalEntry>();
    ns.AddEquals(e => e.FiscalYearRef.Value, typedFy.Id.Value);
    ns.OrderByDescending(e => e.JournalNo.Value);
    ns.Limit(1);
    var last = ns.ExecuteFirstOrDefault();
    var nextNo = 1;
    if (last != null)
    {
        var typedLast = (JournalEntry)last;
        if (typedLast.JournalNo.Value != null) { nextNo = (int)typedLast.JournalNo.Value + 1; }
    }

    // 売上仕訳 (docs/04 の税行方式: 借方 売掛金 / 貸方 売上 + is_tax_line 行)
    var lineCount = (tax > 0) ? 3 : 2;
    var je = new JournalEntry();
    je.EntryDate.Value = AcceptanceDate.Value;
    je.EntryType.Value = "auto";
    je.Description.Value = $"売上計上 {typedSo.Title.Value}（{AcceptanceNo.Value}）";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "acceptance";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(lineCount);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = typedSo.Title.Value;
        if (typedSo.ProjectRef.Value != null) { l.ProjectRef.Value = typedSo.ProjectRef.Value; }
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = arAccountId;
            l.TaxInputMode.Value = "none";
            l.Amount.Value = gross;
            l.InputAmount.Value = gross;
        }
        else if (idx == 2)
        {
            l.Dc.Value = "C";
            l.Account.Value = salesAccountId;
            l.TaxCategory.Value = salesTaxCatId;
            l.TaxInputMode.Value = "none";
            l.Amount.Value = amount;
            l.InputAmount.Value = amount;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = taxAccountId;
            l.TaxCategory.Value = salesTaxCatId;
            l.TaxInputMode.Value = "none";
            l.IsTaxLine.Value = true;
            l.ParentLineNo.Value = 2;
            l.Amount.Value = tax;
            l.InputAmount.Value = tax;
            l.Description.Value = "消費税（行2）";
        }
    }
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("売上仕訳の生成に失敗しました"); return; }

    Status.Value = "confirmed";
    var ret2 = this.Submit();
    if (ret2 != true) { Toaster.Error("検収ステータスの更新に失敗しました（仕訳は生成済みです）"); return; }
    UpdateButtons();
    UpdateOrderProgress();
    Toaster.Success($"仕訳 No.{nextNo} を生成し検収を確定しました（売掛金 {gross:#,0} 円 / 売上 {amount:#,0} 円）");
}

// 請求書番号採番: INV-{西暦下2桁}-{連番3桁} (Invoice 側と同一ロジック)
string NextInvoiceNoForCreate()
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

// 請求書を作成 (confirmed のみ): 受注情報＋受注明細から Invoice を生成
void CreateInvoice_OnClick()
{
    if (Status.Value != "confirmed") { Toaster.Error("確定済みの検収からのみ請求書を作成できます"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 既請求ガード
    var check = new ModuleSearcher<Invoice>();
    check.AddEquals(e => e.AcceptanceRef.Value, this.Id.Value);
    if (check.Execute().Count > 0)
    {
        Toaster.Error("この検収の請求書は既に作成済みです");
        return;
    }

    var os = new ModuleSearcher<SalesOrder>();
    os.AddEquals(e => e.Id.Value, SalesOrderRef.Value);
    var so = os.ExecuteFirstOrDefault();
    if (so == null) { Toaster.Error("受注が見つかりません"); return; }
    var typedSo = (SalesOrder)so;

    var invoiceNo = NextInvoiceNoForCreate();
    var inv = new Invoice();
    inv.InvoiceNo.Value = invoiceNo;
    inv.PartnerRef.Value = typedSo.PartnerRef.Value;
    inv.ProjectRef.Value = typedSo.ProjectRef.Value;
    inv.AcceptanceRef.Value = this.Id.Value;
    inv.Title.Value = typedSo.Title.Value;
    inv.IssueDate.Value = DateOnly.FromDateTime(DateTime.Today);
    inv.DueDate.Value = EndOfNextMonth();
    inv.Status.Value = "issued";
    inv.InvoiceSource.Value = "acceptance";
    inv.Amount.Value = Amount.Value;
    inv.TaxAmount.Value = TaxAmount.Value;

    // 明細は受注明細をコピー
    var ls = new ModuleSearcher<SalesOrderLine>();
    ls.AddEquals(l => l.SalesOrderId.Value, SalesOrderRef.Value);
    ls.OrderBy(l => l.LineNo.Value);
    var srcLines = ls.Execute();
    if (srcLines.Count > 0)
    {
        inv.Lines.AddRows(srcLines.Count);
        var idx = 0;
        foreach (var row in inv.Lines.Rows)
        {
            var dst = (InvoiceLine)row;
            var src = (SalesOrderLine)srcLines[idx];
            idx = idx + 1;
            dst.LineNo.Value = src.LineNo.Value;
            dst.Description.Value = src.Description.Value;
            dst.Qty.Value = src.Qty.Value;
            dst.UnitPrice.Value = src.UnitPrice.Value;
            dst.Amount.Value = src.Amount.Value;
            dst.TaxCategoryRef.Value = src.TaxCategoryRef.Value;
        }
    }

    var ret = inv.Submit();
    if (ret != true) { Toaster.Error("請求書の作成に失敗しました"); return; }

    Toaster.Success($"請求書 {invoiceNo} を作成しました");

    var nsInv = new ModuleSearcher<Invoice>();
    nsInv.AddEquals(e => e.InvoiceNo.Value, invoiceNo);
    var created = nsInv.ExecuteFirstOrDefault();
    if (created != null)
    {
        var typedCreated = (Invoice)created;
        NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("Invoice", $"{typedCreated.Id.Value}"));
    }
}

// 翌月末日 (支払サイト: 月末締め翌月末払いの既定)
DateOnly EndOfNextMonth()
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var firstOfThisMonth = new DateOnly(today.Year, today.Month, 1);
    var firstOfMonthAfterNext = firstOfThisMonth.AddMonths(2);
    return firstOfMonthAfterNext.AddDays(-1);
}
