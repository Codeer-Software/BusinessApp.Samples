// SesBilling.mod.cs — SES 精算・請求（B'-5・表示専用モジュール）
// 責務: SES 案件の月間実績時間（time_entries 合計）に精算幅（下限〜上限h・控除/超過単価）を
//       適用して請求額を計算し、請求書＋売上仕訳（SES売上高 4010）を一括生成する（経理専用）。
// 冪等: invoices の invoice_source='ses' × project × billing_month で生成済みをスキップ。
// 実績時間の月絞り込みは日付文字列比較の罠を避けるため、案件の全工数を取得して
// スクリプト内で WorkDate.Value.Year/Month（型付き比較）でフィルタする（件数は少数の前提）。

// BuildPlan の結果（並行リスト）
List<object> planProjects = new List<object>();
List<int> planHours = new List<int>();
List<int> planMinutes = new List<int>();
List<int> planAmounts = new List<int>();
List<string> planDetails = new List<string>();

void Detail_OnAfterInit()
{
    if (TargetMonth.Value == null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        TargetMonth.Value = new DateOnly(today.Year, today.Month, 1);
    }
}

void Calc_OnClick()
{
    if (TargetMonth.Value == null)
    {
        Toaster.Error("対象月を選択してください");
        return;
    }
    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var picked = TargetMonth.Value;
    var monthFirst = new DateOnly(picked.Year, picked.Month, 1);
    BuildPlan(monthFirst);

    if (planProjects.Count == 0)
    {
        CalcResult.Value = "対象の SES 案件（契約条件設定済み・有効）がありません";
        ResultLabel.Text = "";
        Toaster.Info("対象の SES 案件がありません");
        return;
    }
    var text = "";
    var i = 0;
    foreach (var pm in planProjects)
    {
        var p = (Project)pm;
        text = text + $"{p.Code.Value} {p.Name.Value}: {planDetails[i]}\n";
        i = i + 1;
    }
    CalcResult.Value = text;
    ResultLabel.Text = $"{monthFirst:yyyy年M月}分: SES {planProjects.Count} 件の精算を計算しました（請求書は未生成）";
    Toaster.Success($"SES {planProjects.Count} 件の精算を計算しました");
}

void Run_OnClick()
{
    if (CurrentUser.Role.Value != "accounting" && CurrentUser.Role.Value != "sysadmin")
    {
        Toaster.Error("SES 請求の一括生成（売上計上を伴う）は経理ロールのみ実行できます");
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
    decimal taxPct = 0;
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.Code.Value, "SALES_10");
    var tcat = cs.ExecuteFirstOrDefault();
    if (tcat != null)
    {
        var typedCat = (TaxCategory)tcat;
        salesTaxCatId = typedCat.Id.Value;
        if (typedCat.Rate.Value != null)
        {
            var rs = new ModuleSearcher<TaxRate>();
            rs.AddEquals(r => r.Id.Value, typedCat.Rate.Value);
            var rate = rs.ExecuteFirstOrDefault();
            if (rate != null) { taxPct = ((TaxRate)rate).RatePercent.Value ?? 0; }
        }
    }

    BuildPlan(monthFirst);
    if (planProjects.Count == 0)
    {
        Toaster.Info("対象の SES 案件がありません");
        ResultLabel.Text = "対象の SES 案件（契約条件設定済み・有効）がありません";
        return;
    }

    var created = 0;
    var skipped = 0;
    var journalNos = new List<string>();
    var text = "";
    var i = 0;

    foreach (var pm in planProjects)
    {
        var p = (Project)pm;
        var detail = planDetails[i];
        var amount = planAmounts[i];
        i = i + 1;

        // 冪等ガード: 同案件×同月の SES 請求書が既にあればスキップ
        var chk = new ModuleSearcher<Invoice>();
        chk.AddEquals(v => v.InvoiceSource.Value, "ses");
        chk.AddEquals(v => v.ProjectRef.Value, p.Id.Value);
        chk.AddEquals(v => v.BillingMonth.Value, monthFirst);
        if (chk.Execute().Count > 0)
        {
            skipped = skipped + 1;
            text = text + $"{p.Code.Value} {p.Name.Value}: 生成済みのためスキップ\n";
            continue;
        }
        if (amount <= 0)
        {
            skipped = skipped + 1;
            text = text + $"{p.Code.Value} {p.Name.Value}: 請求額 0 以下のためスキップ（{detail}）\n";
            continue;
        }

        int tax = amount * taxPct / 100;
        int gross = amount + tax;

        // 請求書の生成（明細の摘要に精算内訳を残す）
        var invoiceNo = NextInvoiceNo();
        var invTitle = $"SES精算 {p.Name.Value}（{monthFirst:yyyy年M月}分）";
        var inv = new Invoice();
        inv.InvoiceNo.Value = invoiceNo;
        inv.PartnerRef.Value = p.PartnerRef.Value;
        inv.ProjectRef.Value = p.Id.Value;
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
            line.Description.Value = detail;
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
            ResultLabel.Text = $"中断: 生成 {created}件 / スキップ {skipped}件（{invTitle} で失敗）";
            CalcResult.Value = text;
            return;
        }

        // 生成した請求書の実 Id を取り直す（temporary Id 対策）
        var find = new ModuleSearcher<Invoice>();
        find.AddEquals(v => v.InvoiceNo.Value, invoiceNo);
        var createdInv = find.ExecuteFirstOrDefault();
        if (createdInv == null)
        {
            Toaster.Error($"生成した請求書が見つかりません: {invoiceNo}");
            ResultLabel.Text = $"中断: 生成 {created}件 / スキップ {skipped}件";
            CalcResult.Value = text;
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
            ResultLabel.Text = $"中断: 生成 {created}件 / スキップ {skipped}件（{invoiceNo} の仕訳で失敗）";
            CalcResult.Value = text;
            return;
        }

        journalNos.Add($"No.{nextNo}");
        text = text + $"{p.Code.Value} {p.Name.Value}: {detail} → {invoiceNo}（税込 {gross:#,0} 円・仕訳 No.{nextNo}）\n";
        created = created + 1;
    }

    CalcResult.Value = text;
    var summary = $"{monthFirst:yyyy年M月}分: 生成 {created}件 / スキップ {skipped}件";
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

// 対象月の SES 精算計画を並行リストに構築する
// 実績分は案件ごとの全工数から Year/Month の型付き比較で絞る（日付文字列比較の罠回避）
void BuildPlan(DateOnly monthFirst)
{
    planProjects = new List<object>();
    planHours = new List<int>();
    planMinutes = new List<int>();
    planAmounts = new List<int>();
    planDetails = new List<string>();

    var pjs = new ModuleSearcher<Project>();
    pjs.AddEquals(e => e.ProjectType.Value, "ses");
    pjs.AddEquals(e => e.IsActive.Value, true);
    var projects = pjs.Execute();

    foreach (var pm in projects)
    {
        var p = (Project)pm;
        if (p.SesMonthlyRate.Value == null || p.SesMonthlyRate.Value <= 0) continue;

        // 対象月の実績分（分単位）
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
        var hours = minutes / 60;  // 1h 未満は切捨て（内訳に分を併記）
        var rem = minutes % 60;

        // 精算計算
        int baseRate = p.SesMonthlyRate.Value;
        int amount = baseRate;
        var formula = $"基本 {baseRate:#,0}";
        var lower = p.SesLowerHours.Value;
        var upper = p.SesUpperHours.Value;
        if (lower != null && upper != null)
        {
            formula = formula + $"（精算幅 {lower}〜{upper}h・実績 {hours}h{rem:00}m）";
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
            formula = formula + $"（精算幅なし・実績 {hours}h{rem:00}m） = {amount:#,0}";
        }

        planProjects.Add(pm);
        planHours.Add(hours);
        planMinutes.Add(minutes);
        planAmounts.Add(amount);
        planDetails.Add(formula);
    }
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
