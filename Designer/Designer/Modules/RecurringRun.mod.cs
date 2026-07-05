// RecurringRun.mod.cs — 定期請求の実行（表示専用モジュール）
// 責務: 対象月の有効契約から請求書＋売上仕訳（SaaS）を一括生成する（経理ロール専用）
// 冪等: invoices.recurring_billing_id × billing_month で生成済みをスキップ
// 注: ループ内で ModuleSearcher/Submit を回す N+1 構造だが、契約数は少数（数十件以下）の前提で v1 許容

void Detail_OnAfterInit()
{
    if (TargetMonth.Value == null)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        TargetMonth.Value = new DateOnly(today.Year, today.Month, 1);
    }
}

void Run_OnClick()
{
    if (CurrentUser.Role.Value != "accounting")
    {
        Toaster.Error("定期請求の実行（売上計上を伴う）は経理ロールのみ実行できます");
        return;
    }
    if (TargetMonth.Value == null)
    {
        Toaster.Error("対象月を選択してください");
        return;
    }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 対象月の正規化と基準日 (請求日・仕訳日 = 対象月末日)
    var picked = TargetMonth.Value;
    var monthFirst = new DateOnly(picked.Year, picked.Month, 1);
    var monthEnd = monthFirst.AddMonths(1).AddDays(-1);
    var dueDate = monthFirst.AddMonths(2).AddDays(-1);

    // 会計年度の解決と締め済み期間ガード
    // 注: 月末日 (= 期間の end_date と同日) で >= 比較すると、検索パラメータの時刻付き書式と
    //     seed の素の DATE 文字列の辞書順比較で不一致になる（実測）。境界にならない「月初日」で解決する。
    //     月次期間は月単位なので月初日でも同じ期間・年度に解決される。
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
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("対象月の期間は締め済みです"); return; }

    // 科目・税区分の解決 (売掛金1100 / SaaS売上高4020 / 仮受消費税2200 / SALES_10)
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1100", "4020", "2200");
    var accounts = accS.Execute();
    object arAccountId = null;
    object saasAccountId = null;
    object taxAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1100") { arAccountId = acc.Id.Value; }
        if (acc.Code.Value == "4020") { saasAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2200") { taxAccountId = acc.Id.Value; }
    }
    if (arAccountId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }
    if (saasAccountId == null) { Toaster.Error("SaaS売上高(4020)の科目がありません"); return; }
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

    // 有効な契約を取得し、対象月に該当するものへ生成
    var bs = new ModuleSearcher<RecurringBilling>();
    bs.AddEquals(b => b.IsActive.Value, true);
    var billings = bs.Execute();

    var created = 0;
    var skipped = 0;
    var journalNos = new List<string>();

    foreach (var row in billings)
    {
        var b = (RecurringBilling)row;

        // 契約期間の判定 (開始月 <= 対象月 <= 終了月。終了月 NULL は継続中)
        if (b.StartMonth.Value == null) continue;
        var sm = b.StartMonth.Value;
        var startFirst = new DateOnly(sm.Year, sm.Month, 1);
        if (startFirst > monthFirst) continue;
        if (b.EndMonth.Value != null)
        {
            var em = b.EndMonth.Value;
            var endFirst = new DateOnly(em.Year, em.Month, 1);
            if (endFirst < monthFirst) continue;
        }

        // 冪等ガード: 同契約×同月の請求書が既にあればスキップ
        var chk = new ModuleSearcher<Invoice>();
        chk.AddEquals(i => i.RecurringBillingRef.Value, b.Id.Value);
        chk.AddEquals(i => i.BillingMonth.Value, monthFirst);
        if (chk.Execute().Count > 0)
        {
            skipped = skipped + 1;
            continue;
        }

        int amount = b.MonthlyAmount.Value ?? 0;
        if (amount <= 0)
        {
            skipped = skipped + 1;
            continue;
        }
        int tax = amount * taxPct / 100;
        int gross = amount + tax;

        // 請求書の生成
        var invoiceNo = NextInvoiceNo();
        var invTitle = $"{b.Title.Value}（{monthFirst:yyyy年M月}分）";
        var inv = new Invoice();
        inv.InvoiceNo.Value = invoiceNo;
        inv.PartnerRef.Value = b.PartnerRef.Value;
        inv.ProjectRef.Value = b.ProjectRef.Value;
        inv.Title.Value = invTitle;
        inv.IssueDate.Value = monthEnd;
        inv.DueDate.Value = dueDate;
        inv.Amount.Value = amount;
        inv.TaxAmount.Value = tax;
        inv.Status.Value = "issued";
        inv.InvoiceSource.Value = "recurring";
        inv.RecurringBillingRef.Value = b.Id.Value;
        inv.BillingMonth.Value = monthFirst;
        inv.Lines.AddRows(1);
        foreach (var lineRow in inv.Lines.Rows)
        {
            var line = (InvoiceLine)lineRow;
            line.LineNo.Value = 1;
            line.Description.Value = b.Title.Value;
            line.Qty.Value = 1;
            line.UnitPrice.Value = amount;
            line.Amount.Value = amount;
            line.TaxCategoryRef.Value = salesTaxCatId;
        }
        var retInv = inv.Submit();
        if (retInv != true)
        {
            Toaster.Error($"請求書の生成に失敗しました: {invTitle}");
            ResultLabel.Text = $"中断: 生成 {created}件 / スキップ {skipped}件（{invTitle} で失敗）";
            return;
        }

        // 生成した請求書の実 Id を取り直す (temporary Id 対策)
        var find = new ModuleSearcher<Invoice>();
        find.AddEquals(i => i.InvoiceNo.Value, invoiceNo);
        var createdInv = find.ExecuteFirstOrDefault();
        if (createdInv == null)
        {
            Toaster.Error($"生成した請求書が見つかりません: {invoiceNo}");
            ResultLabel.Text = $"中断: 生成 {created}件 / スキップ {skipped}件";
            return;
        }
        var invoiceId = ((Invoice)createdInv).Id.Value;

        // 売上仕訳 (D 売掛金 税込 / C SaaS売上高 税抜 / C 仮受消費税)
        var nextNo = NextJournalNo(typedFy.Id.Value);
        var lineCount = (tax > 0) ? 3 : 2;
        var je = new JournalEntry();
        je.EntryDate.Value = monthEnd;
        je.EntryType.Value = "auto";
        je.Description.Value = $"SaaS売上 {invTitle}";
        je.Status.Value = "posted";
        je.JournalNo.Value = nextNo;
        je.FiscalYearRef.Value = typedFy.Id.Value;
        je.SourceType.Value = "recurring";
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
            if (b.ProjectRef.Value != null) { l.ProjectRef.Value = b.ProjectRef.Value; }
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
                l.Account.Value = saasAccountId;
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
            return;
        }

        journalNos.Add($"No.{nextNo}");
        created = created + 1;
    }

    var summary = $"{monthFirst:yyyy年M月}分: 生成 {created}件 / スキップ {skipped}件（既生成・対象外）";
    if (journalNos.Count > 0)
    {
        summary = summary + $" / 仕訳 {string.Join(", ", journalNos)}";
    }
    ResultLabel.Text = summary;
    if (created > 0)
    {
        Toaster.Success($"定期請求 {created} 件を生成しました");
    }
    else
    {
        Toaster.Info("生成対象はありませんでした");
    }
}

// 請求書番号採番: INV-{西暦下2桁}-{連番3桁}（Invoice 側と同一ロジック・.Value 規約）
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
