// SesBilling.mod.cs — SES 精算・請求（B'-5・表示専用モジュール。ADR-0036 プラン方式）
// 責務: SES 案件の月間実績時間（time_entries 合計）に精算幅（下限〜上限h・控除/超過単価）を
//       適用して請求額を計算し、請求書＋売上仕訳（SES売上高 4010）を一括生成する（経理専用）。
//
// プラン方式（RecurringRun / ADR-0034 と同パターン）:
//   ・BuildPlan(対象月) が唯一の判定ロジック。実績集計・精算計算・生成済み判定をここだけで行い、
//     結果を ses_run_plan テーブルへ全行洗い替えで書き出す
//   ・プレビュー: 画面の一覧（PlanLines）はプランテーブルを表示するだけ（対象月変更で再構築）
//   ・実行: Run_OnClick は押下時点で BuildPlan を再実行（最新データで再判定）し、
//     status='planned' の行を機械的に消費する。実行側は判定を持たない
//   ・プランテーブルは全ユーザー共有の一時領域（再構築のたび洗い替え。同時プレビューは後勝ちだが
//     実行時に必ず再構築するため会計データはズレない）
// 冪等: 生成済み判定は BuildPlan が invoices（invoice_source='ses' × project × billing_month）から行う
// 実績時間の月絞り込みは日付文字列比較の罠を避けるため、案件の全工数を取得して
// スクリプト内で WorkDate.Value.Year/Month（型付き比較）でフィルタする（件数は少数の前提）。
// 注: ループ内で ModuleSearcher/Submit を回す N+1 構造だが、SES 案件は少数（数十件以下）の前提で v1 許容

void Detail_OnAfterInit()
{
    if (TargetMonth.Value == null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        TargetMonth.Value = new DateOnly(today.Year, today.Month, 1);
    }
    RebuildPlan();
}

void TargetMonth_OnDataChanged()
{
    RebuildPlan();
}

// プランを再構築して一覧を更新する（プレビュー経路）
void RebuildPlan()
{
    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);
    BuildPlan();
    PlanLines.Reload();
}

// 売上税率（SALES_10）の解決。金額計算に使う
decimal SalesTaxPct()
{
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Code.Value, "SALES_10");
    var tcat = cs.ExecuteFirstOrDefault();
    if (tcat == null) { return 0; }
    var typedCat = (TaxCategory)tcat;
    if (typedCat.Rate.Value == null) { return 0; }
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, typedCat.Rate.Value);
    var rate = rs.ExecuteFirstOrDefault();
    if (rate == null) { return 0; }
    return ((TaxRate)rate).RatePercent.Value ?? 0;
}

// ============ プラン構築（唯一の判定ロジック） ============

// 対象月のプラン行を ses_run_plan へ全行洗い替えで書き出す。
// 状態: planned=生成予定 / done=生成済み / excluded=対象外（理由は「内容」列）
void BuildPlan()
{
    // 既存プラン行の全削除（子なしモジュールなので検索インスタンスの Delete() で物理削除できる）
    var clear = new ModuleSearcher<SesRunPlan>();
    foreach (var row in clear.Execute())
    {
        var old = (SesRunPlan)row;
        old.Delete();
    }

    if (TargetMonth.Value == null) return;
    var picked = TargetMonth.Value;
    var monthFirst = new DateOnly(picked.Year, picked.Month, 1);
    var taxPct = SalesTaxPct();

    // 種別 SES の全案件をプラン化（無効・契約条件未設定も「なぜ対象外か」を見せる）
    var pjs = new ModuleSearcher<Project>();
    pjs.AddEquals(e => e.ProjectType.Value, "ses");
    var projects = pjs.Execute();

    foreach (var pm in projects)
    {
        var p = (Project)pm;
        var plan = new SesRunPlan();
        plan.TargetMonth.Value = monthFirst;
        plan.ProjectRef.Value = p.Id.Value;
        plan.PartnerRef.Value = p.PartnerRef.Value;

        if (p.IsActive.Value != true)
        {
            plan.Status.Value = "excluded";
            plan.Detail.Value = "案件が無効化されているため対象外";
            plan.Submit();
            continue;
        }
        if (p.SesMonthlyRate.Value == null || p.SesMonthlyRate.Value <= 0)
        {
            plan.Status.Value = "excluded";
            plan.Detail.Value = "SES 契約条件（月額精算額）が未設定のため対象外";
            plan.Submit();
            continue;
        }

        // 対象月の実績時間（分単位。型付き Year/Month 比較）
        var ts = new ModuleSearcher<TimeEntry>();
        ts.AddEquals(t => t.ProjectRef.Value, p.Id.Value);
        var entries = ts.Execute();
        var minutes = 0;
        foreach (var tm in entries)
        {
            var t = (TimeEntry)tm;
            if (t.WorkDate.Value == null) continue;
            if (t.WorkDate.Value.Year != monthFirst.Year) continue;
            if (t.WorkDate.Value.Month != monthFirst.Month) continue;
            if (t.Minutes.Value == null) continue;
            minutes = minutes + t.Minutes.Value;
        }
        var hours = minutes / 60;  // 1h 未満は切捨て（実績時間に分を併記）
        var rem = minutes % 60;
        plan.ActualTime.Value = $"{hours}h{rem:00}m";

        // 精算計算
        int baseRate = p.SesMonthlyRate.Value;
        int amount = baseRate;
        var formula = $"基本 {baseRate:#,0}";
        var lower = p.SesLowerHours.Value;
        var upper = p.SesUpperHours.Value;
        if (lower != null && upper != null)
        {
            formula = formula + $"（精算幅 {lower}〜{upper}h）";
            if (hours < lower)
            {
                int deduct = p.SesDeductRate.Value ?? 0;
                int shortage = (int)lower - hours;
                amount = baseRate - shortage * deduct;
                formula = formula + $" − 控除 {shortage}h×{deduct:#,0} = {amount:#,0}";
            }
            else if (hours > upper)
            {
                int excess = p.SesExcessRate.Value ?? 0;
                int over = hours - (int)upper;
                amount = baseRate + over * excess;
                formula = formula + $" ＋ 超過 {over}h×{excess:#,0} = {amount:#,0}";
            }
            else
            {
                formula = formula + $" = {amount:#,0}（幅内）";
            }
        }
        else
        {
            formula = formula + $"（精算幅なし） = {amount:#,0}";
        }

        // 生成済み判定（invoice_source='ses' × 案件 × 対象月）
        var chk = new ModuleSearcher<Invoice>();
        chk.AddEquals(v => v.InvoiceSource.Value, "ses");
        chk.AddEquals(v => v.ProjectRef.Value, p.Id.Value);
        chk.AddEquals(v => v.BillingMonth.Value, monthFirst);
        var existing = chk.ExecuteFirstOrDefault();
        if (existing != null)
        {
            var inv = (Invoice)existing;
            plan.InvoiceNo.Value = inv.InvoiceNo.Value;
            plan.InvoiceAmount.Value = inv.Amount.Value;
            plan.TaxAmount.Value = inv.TaxAmount.Value;
            if (inv.Status.Value == "void")
            {
                plan.Status.Value = "excluded";
                plan.Detail.Value = "請求書が取消（void）済みのため再生成しません";
            }
            else
            {
                plan.Status.Value = "done";
                plan.Detail.Value = "SES 請求書＋売上仕訳を生成済み";
            }
            plan.Submit();
            continue;
        }

        if (amount <= 0)
        {
            plan.Status.Value = "excluded";
            plan.Detail.Value = $"請求額 0 円以下のため対象外（{formula}）";
            plan.Submit();
            continue;
        }

        int tax = amount * taxPct / 100;
        plan.Status.Value = "planned";
        plan.InvoiceAmount.Value = amount;
        plan.TaxAmount.Value = tax;
        plan.Detail.Value = formula;
        plan.Submit();
    }
}

// ============ 実行（プラン行の機械的消費。判定は持たない） ============

void Run_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("SES 請求の一括生成（売上計上を伴う）は経理のみ実行できます");
        return;
    }
    if (TargetMonth.Value == null)
    {
        Toaster.Error("対象月を選択してください");
        return;
    }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var picked = TargetMonth.Value;
    var monthFirst = new DateOnly(picked.Year, picked.Month, 1);
    var monthEnd = monthFirst.AddMonths(1).AddDays(-1);
    var dueDate = monthFirst.AddMonths(2).AddDays(-1);

    // 会計年度・期間の解決（境界日の罠回避のため月初日）と締めガード
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("対象月に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("対象月に対応する月次期間がありません"); return; }
    if (((FiscalPeriod)period).Status.Value == "closed") { Toaster.Error("対象月の期間は締め済みです"); return; }

    // 科目・税区分の解決（売掛金1100 / SES売上高4010 / 仮受消費税2200 / SALES_10）
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1100", "4010", "2200");
    var accounts = accS.Execute();
    object arAccountId = null;
    object sesAccountId = null;
    object taxAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1100") { arAccountId = acc.Id.Value; }
        if (acc.Code.Value == "4010") { sesAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2200") { taxAccountId = acc.Id.Value; }
    }
    if (arAccountId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }
    if (sesAccountId == null) { Toaster.Error("SES売上高(4010)の科目がありません"); return; }
    if (taxAccountId == null) { Toaster.Error("仮受消費税(2200)の科目がありません"); return; }

    object salesTaxCatId = null;
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Code.Value, "SALES_10");
    var tcat = cs.ExecuteFirstOrDefault();
    if (tcat != null) { salesTaxCatId = ((TaxCategory)tcat).Id.Value; }

    // 押下時点の最新データでプランを再構築（プレビュー表示が古くてもここが正）
    BuildPlan();

    var pls = new ModuleSearcher<SesRunPlan>();
    pls.AddEquals(e => e.Status.Value, "planned");
    pls.OrderBy(e => e.Id.Value);
    var planRows = pls.Execute();

    var exs = new ModuleSearcher<SesRunPlan>();
    exs.AddEquals(e => e.Status.Value, "excluded");
    var excludedCount = exs.Execute().Count;

    var created = 0;
    var journalNos = new List<string>();

    foreach (var prow in planRows)
    {
        var plan = (SesRunPlan)prow;

        // 案件の取り直し（取引先等、プランに載せない参照のコピー元）
        var fps = new ModuleSearcher<Project>();
        fps.AddEquals(e => e.Id.Value, plan.ProjectRef.Value);
        var foundP = fps.ExecuteFirstOrDefault();
        if (foundP == null) { continue; }
        var p = (Project)foundP;

        int amount = plan.InvoiceAmount.Value ?? 0;
        int tax = plan.TaxAmount.Value ?? 0;
        int gross = amount + tax;
        var formula = plan.Detail.Value;

        // 請求書の生成（明細の摘要に精算内訳を残す）
        var invoiceNo = NextInvoiceNo();
        var invTitle = $"SES精算 {p.Name.Value}（{monthFirst:yyyy年M月}分）";
        var inv = new Invoice();
        inv.InvoiceNo.Value = invoiceNo;
        inv.PartnerRef.Value = p.PartnerRef.Value;
        inv.ProjectRef.Value = p.Id.Value;
        // 部門: SES 精算は案件・契約に部門ソースが無いため「全社共通」を既定にする（部門必須化・2026-07-25）
        inv.DepartmentRef.Value = CommonDepartmentId();
        inv.Title.Value = invTitle;
        inv.IssueDate.Value = monthEnd;
        inv.DueDate.Value = dueDate;
        inv.Amount.Value = amount;
        inv.TaxAmount.Value = tax;
        inv.Status.Value = "issued";
        inv.InvoiceSource.Value = "ses";
        inv.BillingMonth.Value = monthFirst;
        inv.Lines.AddRows(1);
        foreach (var lineRow in inv.Lines.Rows)
        {
            var line = (InvoiceLine)lineRow;
            line.LineNo.Value = 1;
            line.Description.Value = $"{formula}（実績 {plan.ActualTime.Value}）";
            line.Qty.Value = 1;
            line.Unit.Value = "式";
            line.UnitPrice.Value = amount;
            line.Amount.Value = amount;
            line.TaxCategoryRef.Value = salesTaxCatId;
        }
        var retInv = inv.Submit();
        if (retInv != true)
        {
            Toaster.Error($"請求書の生成に失敗しました: {invTitle}");
            ResultLabel.Text = $"中断: 生成 {created}件（{invTitle} で失敗）";
            PlanLines.Reload();
            return;
        }

        // 生成した請求書の実 Id を取り直す（temporary Id 対策）
        var find = new ModuleSearcher<Invoice>();
        find.AddEquals(v => v.InvoiceNo.Value, invoiceNo);
        var createdInv = find.ExecuteFirstOrDefault();
        if (createdInv == null)
        {
            Toaster.Error($"生成した請求書が見つかりません: {invoiceNo}");
            ResultLabel.Text = $"中断: 生成 {created}件";
            PlanLines.Reload();
            return;
        }
        var invoiceId = ((Invoice)createdInv).Id.Value;

        // 売上仕訳（D 売掛金 税込 / C SES売上高 税抜 / C 仮受消費税）— 請求と同時計上（B-5 SaaS と同方式）
        var nextNo = NextJournalNo(typedFy.Id.Value);
        var lineCount = (tax > 0) ? 3 : 2;
        var je = new JournalEntry();
        je.EntryDate.Value = monthEnd;
        je.EntryType.Value = "auto";
        je.Description.Value = invTitle;
        je.Status.Value = "posted";
        je.JournalNo.Value = nextNo;
        je.FiscalYearRef.Value = typedFy.Id.Value;
        je.SourceType.Value = "ses";
        je.SourceId.Value = invoiceId;
        je.Lines.AddRows(lineCount);
        var idx = 0;
        foreach (var jlRow in je.Lines.Rows)
        {
            var l = (JournalLine)jlRow;
            idx = idx + 1;
            l.LineNo.Value = idx;
            l.Description.Value = invTitle;
            l.TaxInputMode.Value = "none";
            l.ProjectRef.Value = p.Id.Value;
            if (idx == 1)
            {
                l.Dc.Value = "D";
                l.Account.Value = arAccountId;
                l.Amount.Value = gross;
                l.InputAmount.Value = gross;
            }
            else if (idx == 2)
            {
                l.Dc.Value = "C";
                l.Account.Value = sesAccountId;
                l.TaxCategory.Value = salesTaxCatId;
                l.Amount.Value = amount;
                l.InputAmount.Value = amount;
            }
            else
            {
                l.Dc.Value = "C";
                l.Account.Value = taxAccountId;
                l.TaxCategory.Value = salesTaxCatId;
                l.IsTaxLine.Value = true;
                l.ParentLineNo.Value = 2;
                l.Amount.Value = tax;
                l.InputAmount.Value = tax;
                l.Description.Value = "消費税（行2）";
            }
        }
        var retJe = je.Submit();
        if (retJe != true)
        {
            Toaster.Error($"売上仕訳の生成に失敗しました: {invTitle}（請求書 {invoiceNo} は作成済み）");
            ResultLabel.Text = $"中断: 生成 {created}件（{invoiceNo} の仕訳で失敗）";
            PlanLines.Reload();
            return;
        }

        journalNos.Add($"No.{nextNo}");
        created = created + 1;
        CreatePendingReceiptFor(invoiceId, invoiceNo);

        plan.Status.Value = "done";
        plan.InvoiceNo.Value = invoiceNo;
        plan.Detail.Value = $"{formula}（仕訳 No.{nextNo}）を生成済み";
        plan.Submit();
    }

    PlanLines.Reload();
    var summary = $"{monthFirst:yyyy年M月}分: 生成 {created}件 / 対象外 {excludedCount}件";
    if (journalNos.Count > 0)
    {
        summary = summary + $" / 仕訳 {string.Join(", ", journalNos)}";
    }
    ResultLabel.Text = summary;
    if (created > 0)
    {
        Toaster.Success($"SES 請求 {created} 件を生成しました");
    }
    else
    {
        Toaster.Info("生成対象はありませんでした");
    }
}

// 「全社共通」(code=00) の部門 Id。SES 精算は案件・契約に部門ソースが無いため既定部門として使う
object CommonDepartmentId()
{
    var s = new ModuleSearcher<Department>();
    s.AddEquals(d => d.Code.Value, "00");
    var found = s.ExecuteFirstOrDefault();
    if (found == null) { return null; }
    return ((Department)found).Id.Value;
}

// 入金予定（未確定入金）の自動作成（Invoice.CreatePendingReceipt と同方針・2026-07-25 ユーザー要望）。
// 発行済み請求書ごとに入金一覧へ「未確定」の行を作り、経理の消込 ToDo にする
void CreatePendingReceiptFor(object invoiceId, string invoiceNo)
{
    var rs = new ModuleSearcher<Receipt>();
    rs.AddEquals(e => e.InvoiceRef.Value, invoiceId);
    if (rs.Execute().Count > 0) { return; }  // 二重作成ガード
    var fs = new ModuleSearcher<Invoice>();
    fs.AddEquals(e => e.Id.Value, invoiceId);
    var found = fs.ExecuteFirstOrDefault();
    if (found == null) { return; }
    var iv = (Invoice)found;
    var r = new Receipt();
    r.InvoiceRef.Value = invoiceId;
    r.ReceiptDate.Value = iv.DueDate.Value;
    r.Method.Value = "bank";
    r.Amount.Value = (iv.Amount.Value ?? 0) + (iv.TaxAmount.Value ?? 0);
    r.Note.Value = "請求書の発行時に自動作成された入金予定です（入金日・金額を実額に修正して確定してください）";
    var ok = r.Submit();
    if (ok != true) { Toaster.Warn($"入金予定の自動作成に失敗しました（{invoiceNo}。入金画面から手動で登録してください）"); }
}

// 請求書番号採番: INV-{西暦下2桁}-{連番3桁}（Invoice / RecurringRun と同一ロジック・.Value 規約）
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

// 伝票番号採番（年度内連番・.Value 規約）
int NextJournalNo(object fiscalYearId)
{
    var ns = new ModuleSearcher<JournalEntry>();
    ns.AddEquals(e => e.FiscalYearRef.Value, fiscalYearId);
    ns.OrderByDescending(e => e.JournalNo.Value);
    ns.Limit(1);
    var last = ns.ExecuteFirstOrDefault();
    var nextNo = 1;
    if (last != null)
    {
        var typedLast = (JournalEntry)last;
        if (typedLast.JournalNo.Value != null) { nextNo = (int)typedLast.JournalNo.Value + 1; }
    }
    return nextNo;
}
