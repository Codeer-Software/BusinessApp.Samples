// Acceptance.mod.cs — 検収
// 責務: 検収番号採番 (A-{yy}-{seq}) / 受注選択時の検収額・消費税の自動セット /
//        検収確定→売上仕訳 (D 売掛金 / C 売上高+仮受消費税。検収基準 = decisions/0008、経理専用) /
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
    var isAccountingRole = CurrentUser.HasAccountingAccess.Value == true;
    ConfirmButton.IsVisible = isAccountingRole && !this.IsNewData && (st == "draft");

    // 確定後は編集できないので保存ボタンは出さない（押せないボタンを見せない・ADR-0027）
    SubmitButton.IsVisible = !(st == "confirmed" && !this.IsNewData);

    // 請求書作成済み（直接 or 合算）ならボタンの代わりに案内ラベルを出す。
    // Invoice モジュールは経理専用（UserReadCondition）のため、general/approver で検索すると
    // "No permission to read module" で画面ごと落ちる——経理アクセスのときだけ照会する
    string invoiceNo = null;
    string billedNo = null;
    if (isAccountingRole)
    {
        invoiceNo = FindExistingInvoiceNo();
        billedNo = FindBilledInvoiceNo();
    }
    CreateInvoiceButton.IsVisible = isAccountingRole && !this.IsNewData && (st == "confirmed") && (invoiceNo == null) && (billedNo == null);
    InvoiceDoneLabel.IsVisible = (invoiceNo != null || billedNo != null);
    if (invoiceNo != null)
    {
        InvoiceDoneLabel.Text = $"請求書 {invoiceNo} 作成済み";
    }
    else if (billedNo != null)
    {
        InvoiceDoneLabel.Text = $"請求書 {billedNo} に合算済み";
    }
    // 「作成済み」と番号を見せている以上、そこへ飛べるようにする（メニューを辿らせない）。
    // 遷移先 Invoice は経理専用モジュールなので、ボタンも経理のときだけ出す
    OpenInvoiceButton.IsVisible = isAccountingRole && (invoiceNo != null || billedNo != null);
    // 確定済みはモジュール全体が閲覧専用＝ボタンの OnClick が発火しないので個別に解除する（FB-030）
    OpenInvoiceButton.IsViewOnly = false;

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

// この検収から作成済みの請求書番号（無ければ null）。
// **取消済み（void）の請求書は数えない。** void は請求の実体を失っており、
// これを「作成済み」と扱っていたため、一度 void にした請求書が検収を永久にロックしていた
// ——再請求ボタンが出ず、確定取消も通らず、下書きに戻せないので削除もできない（BUG-0130）。
string FindExistingInvoiceNo()
{
    if (this.IsNewData) return null;
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.AcceptanceRef.Value, this.Id.Value);
    var found = s.Execute();
    foreach (var row in found)
    {
        var inv = (Invoice)row;
        if (inv.Status.Value == "void") continue;
        return inv.InvoiceNo.Value;
    }
    return null;
}

// 確定の取り消し (confirmed → draft): 売上仕訳を削除して下書きに戻す（経理専用）
// 請求書が既にある場合・仕訳の期間が締め済みの場合は不可（先にそちらを解消する）
void CancelConfirm_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("確定の取り消しは経理のみ実行できます");
        return;
    }
    if (Status.Value != "confirmed") { Toaster.Error("確定済みの検収のみ取り消せます"); return; }
    // 直接請求だけでなく**合算請求も見る**。合算先を見ていなかったため、
    // 合算請求書に載せた検収でも取消が通り、請求書は残ったまま売上仕訳だけが消えていた
    // ——請求額の裏付けとなる売上計上が無い請求書ができる（BUG-0128）
    var invoiceNo = FindExistingInvoiceNo();
    if (invoiceNo != null)
    {
        Toaster.Error($"請求書 {invoiceNo} が作成済みのため取り消せません（先に請求書側を削除してください）");
        return;
    }
    var billedNo = FindBilledInvoiceNo();
    if (billedNo != null)
    {
        Toaster.Error($"請求書 {billedNo} に合算済みのため取り消せません"
            + "（先に請求書からこの検収を外すか、請求書側を削除してください）");
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

// 受注選択: 受注明細を検収明細として取り込む（ADR-0049）。
// 各行の検収金額には「その受注明細の未検収残額（受注明細額 − 確定済み検収額の累計）」を入れる。
// 初回検収では残額＝受注明細の全額。分割検収の 2 回目以降は残額が入り、うっかり全額の再計上を防ぐ。
// 摘要・数量・単位・単価・税区分は受注明細のスナップショット（画面では読み取り専用）。
void SalesOrderRef_OnDataChanged()
{
    if (SalesOrderRef.Value == null)
    {
        Lines.DeleteAllRows();
        RecalcFromLines();
        UpdateOrderProgress();
        return;
    }

    var ls = new ModuleSearcher<SalesOrderLine>();
    ls.AddEquals(l => l.SalesOrderId.Value, SalesOrderRef.Value);
    ls.OrderBy(l => l.LineNo.Value);
    var srcLines = ls.Execute();

    Lines.DeleteAllRows();
    if (srcLines.Count > 0)
    {
        Lines.AddRows(srcLines.Count);
        var idx = 0;
        foreach (var row in Lines.Rows)
        {
            var dst = (AcceptanceLine)row;
            var src = (SalesOrderLine)srcLines[idx];
            idx = idx + 1;
            dst.LineNo.Value = src.LineNo.Value;
            dst.SalesOrderLineId.Value = src.Id.Value;
            dst.Description.Value = src.Description.Value;
            dst.Qty.Value = src.Qty.Value;
            dst.Unit.Value = src.Unit.Value;
            dst.UnitPrice.Value = src.UnitPrice.Value;
            dst.OrderAmount.Value = src.Amount.Value;
            dst.TaxCategoryRef.Value = src.TaxCategoryRef.Value;

            // その受注明細に対する未検収残額
            var ordered = src.Amount.Value ?? 0;
            var accepted = GetConfirmedTotalForOrderLine(src.Id.Value);
            var remaining = ordered - accepted;
            if (remaining < 0) remaining = 0;
            dst.Amount.Value = remaining;
        }
    }

    RecalcFromLines();
    UpdateOrderProgress();
}

// 検収明細の変更 → 行番号の振り直しと合計の再計算
// 再入ガードで囲むのは「明細に書き戻す処理（行番号の振り直し）」だけにする。
// RecalcFromLines はヘッダ（検収額・消費税額）しか触らず Lines を書き換えないので再入しない。
// CLB スクリプトは try/finally を使えないため、ガードの内側に DB 検索（＝例外を出しうる処理）を
// 置くとフラグが true のまま固着し、以後この画面で合計が二度と再計算されなくなる。
void Lines_OnDataChanged()
{
    if (inLinesHandler) return;
    inLinesHandler = true;
    var no = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (AcceptanceLine)row;
        no = no + 1;
        l.LineNo.Value = no;
    }
    inLinesHandler = false;
    RecalcFromLines();
}

bool inLinesHandler = false;

// 検収額・消費税額は明細から導出する（手入力しない・ADR-0049）。
// 消費税は明細の税区分ごとに集計し、税率ごとに 1 回だけ端数処理する（ADR-0050）。
void RecalcFromLines()
{
    var total = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (AcceptanceLine)row;
        if (l.Amount.Value != null) total = total + l.Amount.Value;
    }
    Amount.Value = total;

    decimal defaultPct = GetSalesTaxRatePercent();
    var rates = new List<decimal>();
    var bases = new List<int>();
    foreach (var row in Lines.Rows)
    {
        var l = (AcceptanceLine)row;
        if (l.Amount.Value == null) continue;
        decimal pct = l.TaxCategoryRef.Value == null
            ? defaultPct
            : ResolveTaxableSalesRatePercent(l.TaxCategoryRef.Value);
        if (pct <= 0) continue;
        var idx = rates.IndexOf(pct);
        if (idx < 0) { rates.Add(pct); bases.Add(l.Amount.Value); }
        else { bases[idx] = bases[idx] + l.Amount.Value; }
    }
    var tax = 0;
    for (var i = 0; i < rates.Count; i++)
    {
        tax = tax + (int)(bases[i] * rates[i] / 100);
    }
    TaxAmount.Value = tax;
}

// ある受注明細に対する確定済み検収額の累計（この検収自身は除く）
int GetConfirmedTotalForOrderLine(long? salesOrderLineId)
{
    if (salesOrderLineId == null) return 0;
    var s = new ModuleSearcher<AcceptanceLine>();
    s.AddEquals(l => l.SalesOrderLineId.Value, salesOrderLineId);
    var found = s.Execute();
    var sum = 0;
    foreach (var m in found)
    {
        var al = (AcceptanceLine)m;
        if (al.AcceptanceId.Value == this.Id.Value) continue;   // 編集中の自分の行は除く
        var acc = new ModuleSearcher<Acceptance>();
        acc.AddEquals(a => a.Id.Value, al.AcceptanceId.Value);
        var accFound = acc.ExecuteFirstOrDefault();
        if (accFound == null) continue;
        if (((Acceptance)accFound).Status.Value != "confirmed") continue;
        if (al.Amount.Value != null) sum = sum + al.Amount.Value;
    }
    return sum;
}

// 税区分 ID → 課税売上ならその税率(%)、それ以外・未設定なら 0
decimal ResolveTaxableSalesRatePercent(long? taxCategoryId)
{
    if (taxCategoryId == null) return 0;
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Id.Value, taxCategoryId);
    var found = cs.ExecuteFirstOrDefault();
    if (found == null) return 0;
    var tcat = (TaxCategory)found;
    if (tcat.TaxationType.Value != "taxable_sales") return 0;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    return ((TaxRate)foundRate).RatePercent.Value ?? 0;
}

// この検収の合算先請求書の番号（未設定なら null）。
// FindExistingInvoiceNo と同じく**取消済み（void）は数えない**（BUG-0130）
string FindBilledInvoiceNo()
{
    if (BilledInvoiceRef.Value == null) return null;
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.Id.Value, BilledInvoiceRef.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    var billed = (Invoice)found;
    if (billed.Status.Value == "void") return null;
    return billed.InvoiceNo.Value;
}

// 「請求書 INV-xx-xxx 作成済み」から、その請求書の詳細へ飛ぶ（直接請求 → 合算先の順に解決）。
// 遷移はフレーム非依存の GetModuleDataUrl で組む（固定パスはフレーム改名で静かに 404 になる）
void OpenInvoice_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("請求書の参照は経理のみ実行できます");
        return;
    }
    object invoiceId = null;
    var s = new ModuleSearcher<Invoice>();
    s.AddEquals(e => e.AcceptanceRef.Value, this.Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found != null) { invoiceId = ((Invoice)found).Id.Value; }
    else if (BilledInvoiceRef.Value != null) { invoiceId = BilledInvoiceRef.Value; }

    if (invoiceId == null)
    {
        Toaster.Warn("この検収に対応する請求書が見つかりません");
        return;
    }
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("Invoice", $"{invoiceId}"));
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

// 売上伝票の既定税区分の税率(%)。税区分が未設定の明細に対する保険的な既定として使う。
// 既定の税区分は税制マスタ（tax_categories.default_for='sales'）で設定する（ADR-0050）
decimal GetSalesTaxRatePercent()
{
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.DefaultFor.Value, "sales");
    cs.AddEquals(c => c.IsActive.Value, true);
    var found = cs.ExecuteFirstOrDefault();
    if (found == null) return 0;
    return ResolveTaxableSalesRatePercent(((TaxCategory)found).Id.Value);
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

// 検収確定: 売上仕訳を生成して confirmed へ (経理専用)
// D 売掛金1100 (税込) / C 売上科目 (税抜, 案件区分で 4000/4010/4020) / C 仮受消費税2200 (税行)
void Confirm_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("検収の確定（売上計上）は経理のみ実行できます");
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

    // 明細の税区分ごとに「売上行（＋消費税行）」を作る（ADR-0050）。
    // 一律 SALES_10 で作ってはいけない——税額そのものは RecalcFromLines が
    // 税率別に正しく出しているが、仕訳行に付ける税区分が実態と違うと
    // 消費税集計表（journal_lines.tax_category_id で集計）が壊れる。
    // 非課税が混ざれば課税売上割合が 100% に化け、8% が混ざれば税率別内訳が出せない。
    var catKeys = new List<string>();
    var catIds = new List<object>();
    var catNets = new List<int>();
    var catRates = new List<decimal>();
    decimal defaultPct = GetSalesTaxRatePercent();
    foreach (var row in Lines.Rows)
    {
        var l = (AcceptanceLine)row;
        if (l.Amount.Value == null) continue;
        var cid = l.TaxCategoryRef.Value;
        decimal pct = defaultPct;
        if (cid == null)
        {
            // 明細に税区分が無い行のフォールバック先が無いまま進むと、税区分 null の売上行が
            // MarkRemainingLinesOutOfScope() で黙って「対象外」に落ち、課税売上が消費税集計から丸ごと消える。
            // 静かに壊すより止める（SALES_10 を改名・無効化したときにここで気づける）
            if (salesTaxCatId == null)
            {
                Toaster.Error("税区分が設定されていない明細があり、既定の課税売上(SALES_10)も見つかりません。"
                    + "明細の税区分を指定するか、税区分マスタを確認してください");
                return;
            }
            cid = salesTaxCatId;
        }
        else { pct = ResolveTaxableSalesRatePercent(l.TaxCategoryRef.Value); }
        var key = $"{cid}";
        var ci = catKeys.IndexOf(key);
        if (ci < 0)
        {
            catKeys.Add(key);
            catIds.Add(cid);
            catNets.Add(l.Amount.Value);
            catRates.Add(pct);
        }
        else { catNets[ci] = catNets[ci] + l.Amount.Value; }
    }
    if (catKeys.Count == 0) { Toaster.Error("検収明細がありません"); return; }

    // 税区分ごとの税額。**合計はヘッダの消費税額と 1 円もずれてはいけない**
    // （借方の売掛金を amount + tax で作るので、ずれると貸借が合わなくなる）。
    // ヘッダ側は税率ごとに 1 回端数処理しているので、同率の税区分が複数あると
    // 区分ごとの端数処理と最大 1 円ずれる。差は最後の課税区分で吸収する
    var catTaxes = new List<int>();
    var lastTaxable = -1;
    var taxSum = 0;
    for (var i = 0; i < catKeys.Count; i++)
    {
        var t = 0;
        if (catRates[i] > 0)
        {
            t = (int)(catNets[i] * catRates[i] / 100);
            lastTaxable = i;
        }
        catTaxes.Add(t);
        taxSum = taxSum + t;
    }
    if (lastTaxable >= 0 && taxSum != tax)
    {
        catTaxes[lastTaxable] = catTaxes[lastTaxable] + (tax - taxSum);
    }


    // 売上仕訳 (docs/04 の税行方式: 借方 売掛金 / 貸方 売上 + is_tax_line 行)
    //
    // **行数を数える変数に `.Count` を代入してはいけない**（2026-08-17 実測・FB-053）。
    // `AddRows(list.Count)` のようにその場で渡すのは動くが、
    // `var n = list.Count;` と受けた変数を `AddRows(n)` に渡すと実行時に
    // `Value cannot be null. (Parameter 'source')` で落ちる。designcheck は緑のまま素通りする。
    // このコードベースで動いている 6 箇所はすべて**整数リテラルから始めて加算するだけ**なので、それに揃える。
    var lineCount = 1;                       // 1 行目 = 借方 売掛金（税込総額）
    for (var i = 0; i < catKeys.Count; i++)  // ループ条件での .Count 参照は動く
    {
        lineCount = lineCount + 1;                              // 税区分ごとの売上行
        if (catTaxes[i] != 0) { lineCount = lineCount + 1; }    // 税額がある区分は消費税行も
    }
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
    var pos = 0;

    // 1 行目: 借方 売掛金（税込総額）
    var dr = (JournalLine)je.Lines.Rows[pos];
    dr.LineNo.Value = 1;
    dr.Description.Value = typedSo.Title.Value;
    if (typedSo.ProjectRef.Value != null) { dr.ProjectRef.Value = typedSo.ProjectRef.Value; }
    if (typedSo.DepartmentRef.Value != null) { dr.Department.Value = typedSo.DepartmentRef.Value; }
    dr.Dc.Value = "D";
    dr.Account.Value = arAccountId;
    dr.TaxInputMode.Value = "none";
    dr.Amount.Value = gross;
    dr.InputAmount.Value = gross;
    pos = pos + 1;

    // 税区分ごとに 貸方 売上 → その消費税行、の順に積む
    for (var i = 0; i < catKeys.Count; i++)
    {
        var salesLineNo = pos + 1;
        var sl = (JournalLine)je.Lines.Rows[pos];
        sl.LineNo.Value = salesLineNo;
        sl.Description.Value = typedSo.Title.Value;
        if (typedSo.ProjectRef.Value != null) { sl.ProjectRef.Value = typedSo.ProjectRef.Value; }
        if (typedSo.DepartmentRef.Value != null) { sl.Department.Value = typedSo.DepartmentRef.Value; }
        sl.Dc.Value = "C";
        sl.Account.Value = salesAccountId;
        sl.TaxCategory.Value = catIds[i];
        sl.TaxInputMode.Value = "none";
        sl.Amount.Value = catNets[i];
        sl.InputAmount.Value = catNets[i];
        pos = pos + 1;

        if (catTaxes[i] == 0) continue;
        var tl = (JournalLine)je.Lines.Rows[pos];
        tl.LineNo.Value = pos + 1;
        tl.Description.Value = $"消費税（行{salesLineNo}）";
        if (typedSo.ProjectRef.Value != null) { tl.ProjectRef.Value = typedSo.ProjectRef.Value; }
        if (typedSo.DepartmentRef.Value != null) { tl.Department.Value = typedSo.DepartmentRef.Value; }
        tl.Dc.Value = "C";
        tl.Account.Value = taxAccountId;
        tl.TaxCategory.Value = catIds[i];
        tl.TaxInputMode.Value = "none";
        tl.IsTaxLine.Value = true;
        tl.ParentLineNo.Value = salesLineNo;
        tl.Amount.Value = catTaxes[i];
        tl.InputAmount.Value = catTaxes[i];
        pos = pos + 1;
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
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

    // 既請求ガード。**void（取消済み）の請求書は数えない**——
    // ボタンの出し分け（FindExistingInvoiceNo）は void を除外しているので、
    // ここだけ除外しないと「ボタンは出るのに押すと既作成エラー」という片手落ちになる（BUG-0130）
    if (FindExistingInvoiceNo() != null)
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
    inv.DepartmentRef.Value = typedSo.DepartmentRef.Value;
    inv.AcceptanceRef.Value = this.Id.Value;
    inv.Title.Value = typedSo.Title.Value;
    inv.IssueDate.Value = DateOnly.FromDateTime(DateTime.Today);
    inv.DueDate.Value = EndOfNextMonth();
    // 下書きで生成し、ユーザーが請求書画面で明示的に「発行する」を押して発行済みにする
    // （自動で発行済みになるのは驚きが大きい・2026-07-21 ユーザー指摘。
    //   定期請求・SES の一括生成は月次発行バッチなので従来どおり issued 直書き）
    inv.Status.Value = "draft";
    inv.InvoiceSource.Value = "acceptance";
    inv.Amount.Value = Amount.Value;
    inv.TaxAmount.Value = TaxAmount.Value;

    // 明細は「検収明細」をコピーする（ADR-0049）。
    // 受注明細ではなく検収明細を写すことで、請求書の明細合計＝検収額が構造的に成立し、
    // 請求書を開き直しても金額が受注全額に戻らない（改善候補 A-1 の根治）。
    var ls = new ModuleSearcher<AcceptanceLine>();
    ls.AddEquals(l => l.AcceptanceId.Value, this.Id.Value);
    ls.OrderBy(l => l.LineNo.Value);
    var srcLines = ls.Execute();
    if (srcLines.Count > 0)
    {
        inv.Lines.AddRows(srcLines.Count);
        var idx = 0;
        foreach (var row in inv.Lines.Rows)
        {
            var dst = (InvoiceLine)row;
            var src = (AcceptanceLine)srcLines[idx];
            idx = idx + 1;
            dst.LineNo.Value = src.LineNo.Value;
            dst.AcceptanceLineRef.Value = src.Id.Value;   // 行単位の超過判定の根拠
            dst.Description.Value = src.Description.Value;
            dst.Qty.Value = src.Qty.Value;
            dst.Unit.Value = src.Unit.Value;
            dst.UnitPrice.Value = src.UnitPrice.Value;
            dst.Amount.Value = src.Amount.Value;          // 受注額ではなく検収額
            dst.TaxCategoryRef.Value = src.TaxCategoryRef.Value;
        }
    }

    var ret = inv.Submit();
    if (ret != true) { Toaster.Error("請求書の作成に失敗しました"); return; }

    Toaster.Success($"請求書 {invoiceNo} を下書きで作成しました（内容を確認して「発行する」を押してください）");

    var nsInv = new ModuleSearcher<Invoice>();
    nsInv.AddEquals(e => e.InvoiceNo.Value, invoiceNo);
    var created = nsInv.ExecuteFirstOrDefault();
    if (created != null)
    {
        var typedCreated = (Invoice)created;
        // Invoice は SalesBilling フレームにのみ登録されている。営業×経理の兼務者が SalesStaff 側で
        // 検収を確定した場合に現在フレーム解決だと未登録で真っ白になる（FB-023）ため、フレームを明示する
        NavigationService.NavigateTo($"/SalesBilling/Invoice/{typedCreated.Id.Value}");
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
