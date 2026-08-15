// RecurringRun.mod.cs — 定期請求の実行（表示専用モジュール。ADR-0034 プラン方式）
// 責務: 対象月の有効契約から請求書＋売上仕訳（SaaS）＋前受収益の按分振替を一括生成する（経理専用）
//
// プラン方式（プレビューと実行のロジック共通化）:
//   ・BuildPlan(対象月) が唯一の判定ロジック。期間・周期・金額・生成済みの判定をここだけで行い、
//     結果を recurring_run_plan テーブルへ全行洗い替えで書き出す
//   ・プレビュー: 画面の一覧（PlanLines）はプランテーブルを表示するだけ（対象月変更で再構築）
//   ・実行: Run_OnClick は押下時点で BuildPlan を再実行（最新データで再判定）し、
//     status='planned' の行を plan_kind に従って機械的に消費する。実行側は判定を持たない
//   ・プランテーブルは全ユーザー共有の一時領域（再構築のたび洗い替え。同時プレビューは後勝ちだが
//     実行時に必ず再構築するため会計データはズレない）
// 冪等: 生成済み判定は BuildPlan が invoices / journal_entries の実データから行う
// 注: ループ内で ModuleSearcher/Submit を回す N+1 構造だが、契約数は少数（数十件以下）の前提で v1 許容

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

// ============ プラン構築（唯一の判定ロジック） ============

// 対象月のプラン行を recurring_run_plan へ全行洗い替えで書き出す。
// 状態: planned=生成予定 / done=生成済み / excluded=対象外（理由は Detail 列）
// 処理種別 plan_kind: monthly=月額請求＋売上仕訳 / annual=年額請求＋前受計上＋按分振替 /
//                     defer=按分振替のみ / none=実行対象なし
void BuildPlan()
{
    // 既存プラン行の全削除（子なしモジュールなので検索インスタンスの Delete() で物理削除できる）
    var clear = new ModuleSearcher<RecurringRunPlan>();
    foreach (var row in clear.Execute())
    {
        var old = (RecurringRunPlan)row;
        old.Delete();
    }

    if (TargetMonth.Value == null) return;
    var picked = TargetMonth.Value;
    var monthFirst = new DateOnly(picked.Year, picked.Month, 1);

    // 税率の解決（売上の既定税区分＝税制マスタで設定。金額計算に使う）
    decimal taxPct = 0;
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.DefaultFor.Value, "sales");
    cs.AddEquals(c => c.IsActive.Value, true);
    var tcat = cs.ExecuteFirstOrDefault();
    if (tcat != null)
    {
        var typedCat = (TaxCategory)tcat;
        if (typedCat.Rate.Value != null)
        {
            var trs = new ModuleSearcher<TaxRate>();
            trs.AddEquals(r => r.Id.Value, typedCat.Rate.Value);
            var rate = trs.ExecuteFirstOrDefault();
            if (rate != null) { taxPct = ((TaxRate)rate).RatePercent.Value ?? 0; }
        }
    }

    // 期間該当する全契約を状態つきでプラン化（下書き・無効も「なぜ対象外か」を見せる）
    var bs = new ModuleSearcher<RecurringBilling>();
    var billings = bs.Execute();
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

        if (b.Status.Value != "confirmed")
        {
            var pd = (RecurringRunPlan)NewPlanRow(monthFirst, b);
            pd.PlanKind.Value = "none";
            pd.Status.Value = "excluded";
            // 終了した契約は通常なら終了月の判定で上の continue に落ちるが、
            // 終了月より前の月を対象に実行するとここへ来る（ADR-0057）
            if (b.Status.Value == "ended")
            {
                pd.Detail.Value = "契約が終了しているため対象外";
            }
            else
            {
                pd.Detail.Value = "契約が下書き（経理確認前）のため対象外";
            }
            pd.Submit();
            continue;
        }
        if (b.IsActive.Value != true)
        {
            var pi = (RecurringRunPlan)NewPlanRow(monthFirst, b);
            pi.PlanKind.Value = "none";
            pi.Status.Value = "excluded";
            pi.Detail.Value = "契約が無効化されているため対象外";
            pi.Submit();
            continue;
        }

        if (b.BillingCycle.Value == "yearly")
        {
            BuildYearlyPlanRow(monthFirst, b, taxPct);
        }
        else
        {
            BuildMonthlyPlanRow(monthFirst, b, taxPct);
        }
    }
}

// プラン行の共通項目（契約・取引先・部門・種別）をセットした新規行を返す
object NewPlanRow(DateOnly monthFirst, object billing)
{
    var b = (RecurringBilling)billing;
    var p = new RecurringRunPlan();
    p.TargetMonth.Value = monthFirst;
    p.ContractRef.Value = b.Id.Value;
    p.PartnerRef.Value = b.PartnerRef.Value;
    p.DepartmentRef.Value = b.DepartmentRef.Value;
    if (b.BillingCycle.Value == "yearly")
    {
        p.Cycle.Value = "yearly";
    }
    else
    {
        p.Cycle.Value = "monthly";
    }
    return p;
}

// 月額契約のプラン行
void BuildMonthlyPlanRow(DateOnly monthFirst, object billing, decimal taxPct)
{
    var b = (RecurringBilling)billing;
    var p = (RecurringRunPlan)NewPlanRow(monthFirst, billing);

    // 生成済み判定（同契約×同月の請求書）
    var chk = new ModuleSearcher<Invoice>();
    chk.AddEquals(i => i.RecurringBillingRef.Value, b.Id.Value);
    chk.AddEquals(i => i.BillingMonth.Value, monthFirst);
    var existing = chk.ExecuteFirstOrDefault();
    if (existing != null)
    {
        var inv = (Invoice)existing;
        p.PlanKind.Value = "none";
        p.InvoiceAmount.Value = inv.Amount.Value;
        p.TaxAmount.Value = inv.TaxAmount.Value;
        p.InvoiceNo.Value = inv.InvoiceNo.Value;
        if (inv.Status.Value == "void")
        {
            p.Status.Value = "excluded";
            p.Detail.Value = "請求書が取消（void）済みのため再生成しません";
        }
        else
        {
            p.Status.Value = "done";
            p.Detail.Value = "月額請求書＋売上仕訳を生成済み";
        }
        p.Submit();
        return;
    }

    int amount = b.MonthlyAmount.Value ?? 0;
    if (amount <= 0)
    {
        p.PlanKind.Value = "none";
        p.Status.Value = "excluded";
        p.Detail.Value = "月額金額が未設定（0円）のため対象外";
        p.Submit();
        return;
    }
    int tax = amount * taxPct / 100;
    p.PlanKind.Value = "monthly";
    p.Status.Value = "planned";
    p.InvoiceAmount.Value = amount;
    p.TaxAmount.Value = tax;
    p.Detail.Value = "月額請求書＋売上仕訳を生成";
    p.Submit();
}

// 年払い契約のプラン行（周期判定込み。会計処理は ADR-0033 までの仕様と同一）
void BuildYearlyPlanRow(DateOnly monthFirst, object billing, decimal taxPct)
{
    var b = (RecurringBilling)billing;
    var p = (RecurringRunPlan)NewPlanRow(monthFirst, billing);

    int annual = b.AnnualAmount.Value ?? 0;
    if (annual <= 0)
    {
        p.PlanKind.Value = "none";
        p.Status.Value = "excluded";
        p.Detail.Value = "年額金額が未設定（0円）のため対象外";
        p.Submit();
        return;
    }

    // 対象月が属する年次周期 (開始月の応当月から12ヶ月。応当月ごとに自動更新)
    var sm = b.StartMonth.Value;
    var startFirst = new DateOnly(sm.Year, sm.Month, 1);
    var offsetMonths = (monthFirst.Year - startFirst.Year) * 12 + (monthFirst.Month - startFirst.Month);
    var cycleIndex = offsetMonths % 12;
    var cycleStart = monthFirst.AddMonths(-cycleIndex);
    int annualTax = annual * taxPct / 100;
    int portionBase = annual / 12;
    int portion = portionBase;
    if (cycleIndex == 11) { portion = annual - portionBase * 11; }
    var monthNo = cycleIndex + 1;

    p.CycleStart.Value = cycleStart;
    p.CycleIndex.Value = cycleIndex;
    p.DeferAmount.Value = portion;

    // アンカー年額請求書の有無（冪等: 契約×周期起点月）
    var invChk = new ModuleSearcher<Invoice>();
    invChk.AddEquals(i => i.RecurringBillingRef.Value, b.Id.Value);
    invChk.AddEquals(i => i.BillingMonth.Value, cycleStart);
    var annualInvRow = invChk.ExecuteFirstOrDefault();

    if (annualInvRow == null)
    {
        if (cycleIndex != 0)
        {
            // 周期の途中月だが年額請求書が未生成 (起点月が未実行)。起点月から順に実行する運用
            p.PlanKind.Value = "none";
            p.Status.Value = "excluded";
            p.DeferAmount.Value = null;
            p.Detail.Value = $"周期起点月（{cycleStart:yyyy年M月}）が未実行のため対象外（起点月から順に実行してください）";
            p.Submit();
            return;
        }
        p.PlanKind.Value = "annual";
        p.Status.Value = "planned";
        p.InvoiceAmount.Value = annual;
        p.TaxAmount.Value = annualTax;
        p.Detail.Value = $"年額請求書＋前受計上＋按分振替（{monthNo}/12ヶ月目）を生成";
        p.Submit();
        return;
    }

    var anchorInv = (Invoice)annualInvRow;
    p.InvoiceNo.Value = anchorInv.InvoiceNo.Value;
    p.AnnualInvoiceId.Value = anchorInv.Id.Value;

    // 取消（void）された年額請求書は按分振替の対象から外す（ADR-0033。
    // 期間が締め済みで「発行を取り消す」（全削除）が使えない場合の停止手段）
    if (anchorInv.Status.Value == "void")
    {
        p.PlanKind.Value = "none";
        p.Status.Value = "excluded";
        p.DeferAmount.Value = null;
        p.Detail.Value = "年額請求書が取消（void）済みのため按分振替を停止中";
        p.Submit();
        return;
    }

    // 当月分の按分振替の有無（冪等: 同一 source の既存仕訳から月一致で判定。
    //  日付範囲検索は境界日の罠があるため使わない）
    var dchk = new ModuleSearcher<JournalEntry>();
    dchk.AddEquals(e => e.SourceType.Value, "recurring_defer");
    dchk.AddEquals(e => e.SourceId.Value, anchorInv.Id.Value);
    var defers = dchk.Execute();
    var already = false;
    foreach (var dRow in defers)
    {
        var de = (JournalEntry)dRow;
        if (de.EntryDate.Value != null
            && de.EntryDate.Value.Year == monthFirst.Year
            && de.EntryDate.Value.Month == monthFirst.Month)
        {
            already = true;
            break;
        }
    }
    if (already)
    {
        p.PlanKind.Value = "none";
        p.Status.Value = "done";
        p.Detail.Value = $"按分振替（{monthNo}/12ヶ月目）を生成済み";
        p.Submit();
        return;
    }
    p.PlanKind.Value = "defer";
    p.Status.Value = "planned";
    p.Detail.Value = $"按分振替のみ（{monthNo}/12ヶ月目）を生成";
    p.Submit();
}

// ============ 実行（プラン行の機械的消費。判定は持たない） ============

void Run_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("定期請求の実行（売上計上を伴う）は経理のみ実行できます");
        return;
    }
    if (TargetMonth.Value == null)
    {
        Toaster.Error("対象月を選択してください");
        return;
    }

    // 一括操作なので確認する（ADR-0062）。押した本数が一度に会計データになる
    var picked0 = TargetMonth.Value;
    var plannedCount = 0;
    var cs0 = new ModuleSearcher<RecurringRunPlan>();
    cs0.AddEquals(e => e.Status.Value, "planned");
    plannedCount = cs0.Execute().Count;
    if (plannedCount == 0)
    {
        Toaster.Info("生成対象はありませんでした");
        return;
    }
    var answer = MessageBox.Show(
        $"{picked0:yyyy年M月}分として、一覧の「生成予定」{plannedCount} 件から請求書と仕訳を作成します。よろしいですか？",
        "実行する", "キャンセル");
    if (answer != "実行する") return;

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

    // 科目・税区分の解決 (売掛金1100 / SaaS売上高4020 / 仮受消費税2200 / 前受収益2110 / 売上の既定税区分)
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "1100", "4020", "2200", "2110");
    var accounts = accS.Execute();
    object arAccountId = null;
    object saasAccountId = null;
    object taxAccountId = null;
    object deferredAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1100") { arAccountId = acc.Id.Value; }
        if (acc.Code.Value == "4020") { saasAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2200") { taxAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2110") { deferredAccountId = acc.Id.Value; }
    }
    if (arAccountId == null) { Toaster.Error("売掛金(1100)の科目がありません"); return; }
    if (saasAccountId == null) { Toaster.Error("SaaS売上高(4020)の科目がありません"); return; }
    if (taxAccountId == null) { Toaster.Error("仮受消費税(2200)の科目がありません"); return; }
    if (deferredAccountId == null) { Toaster.Error("前受収益(2110)の科目がありません"); return; }

    // 売上の既定税区分は税制マスタで設定する（tax_categories.default_for='sales'）
    object salesTaxCatId = null;
    var cs = new ModuleSearcher<TaxCategory>();
    cs.AddEquals(c => c.DefaultFor.Value, "sales");
    cs.AddEquals(c => c.IsActive.Value, true);
    var tcat = cs.ExecuteFirstOrDefault();
    if (tcat != null) { salesTaxCatId = ((TaxCategory)tcat).Id.Value; }

    // 押下時点の最新データでプランを再構築（プレビュー表示が古くてもここが正）
    BuildPlan();

    var pls = new ModuleSearcher<RecurringRunPlan>();
    pls.AddEquals(e => e.Status.Value, "planned");
    pls.OrderBy(e => e.Id.Value);
    var planRows = pls.Execute();

    var exs = new ModuleSearcher<RecurringRunPlan>();
    exs.AddEquals(e => e.Status.Value, "excluded");
    var excludedCount = exs.Execute().Count;

    var created = 0;
    var annualCreated = 0;
    var deferCreated = 0;
    var journalNos = new List<string>();

    foreach (var prow in planRows)
    {
        var plan = (RecurringRunPlan)prow;

        // 契約の取り直し（プロジェクト等、プランに載せない参照のコピー元）
        var cbs = new ModuleSearcher<RecurringBilling>();
        cbs.AddEquals(e => e.Id.Value, plan.ContractRef.Value);
        var foundB = cbs.ExecuteFirstOrDefault();
        if (foundB == null) { continue; }
        var b = (RecurringBilling)foundB;

        // ===== 月額: 請求書＋売上仕訳 =====
        if (plan.PlanKind.Value == "monthly")
        {
            int amount = plan.InvoiceAmount.Value ?? 0;
            int tax = plan.TaxAmount.Value ?? 0;
            int gross = amount + tax;

            var invoiceNo = NextInvoiceNo();
            var invTitle = $"{b.Title.Value}（{monthFirst:yyyy年M月}分）";
            var inv = new Invoice();
            inv.InvoiceNo.Value = invoiceNo;
            inv.PartnerRef.Value = b.PartnerRef.Value;
            inv.ProjectRef.Value = b.ProjectRef.Value;
            inv.DepartmentRef.Value = b.DepartmentRef.Value;
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
                line.Unit.Value = "式";
                line.UnitPrice.Value = amount;
                line.Amount.Value = amount;
                line.TaxCategoryRef.Value = salesTaxCatId;
            }
            var retInv = inv.Submit();
            if (retInv != true)
            {
                Toaster.Error($"請求書の生成に失敗しました: {invTitle}");
                ResultLabel.Text = $"中断: 月額 {created}件 / 年額請求 {annualCreated}件 / 按分振替 {deferCreated}件（{invTitle} で失敗）";
                PlanLines.Reload();
                return;
            }

            // 生成した請求書の実 Id を取り直す (temporary Id 対策)
            var find = new ModuleSearcher<Invoice>();
            find.AddEquals(i => i.InvoiceNo.Value, invoiceNo);
            var createdInv = find.ExecuteFirstOrDefault();
            if (createdInv == null)
            {
                Toaster.Error($"生成した請求書が見つかりません: {invoiceNo}");
                PlanLines.Reload();
                return;
            }
            var invoiceId = ((Invoice)createdInv).Id.Value;

            // 売上仕訳 (D 売掛金 税込 / C SaaS売上高 税抜 / C 仮受消費税)
            var nextNo = NextJournalNo(typedFy.Id.Value);
            var lineCount = 2;
            if (tax > 0) { lineCount = 3; }
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
                if (b.DepartmentRef.Value != null) { l.Department.Value = b.DepartmentRef.Value; }
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
            je.MarkRemainingLinesOutOfScope();
            je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
            var retJe = je.Submit();
            if (retJe != true)
            {
                Toaster.Error($"売上仕訳の生成に失敗しました: {invTitle}（請求書 {invoiceNo} は作成済み）");
                ResultLabel.Text = $"中断: 月額 {created}件 / 年額請求 {annualCreated}件 / 按分振替 {deferCreated}件（{invoiceNo} の仕訳で失敗）";
                PlanLines.Reload();
                return;
            }

            journalNos.Add($"No.{nextNo}");
            created = created + 1;
            CreatePendingReceiptFor(invoiceId, invoiceNo);

            plan.Status.Value = "done";
            plan.InvoiceNo.Value = invoiceNo;
            plan.Detail.Value = $"月額請求書＋売上仕訳（No.{nextNo}）を生成済み";
            plan.Submit();
            continue;
        }

        // ===== 年払い（周期起点月）: 年額請求書＋前受計上＋当月分の按分振替 =====
        if (plan.PlanKind.Value == "annual")
        {
            int annual = plan.InvoiceAmount.Value ?? 0;
            int annualTax = plan.TaxAmount.Value ?? 0;
            int annualGross = annual + annualTax;
            int portion = plan.DeferAmount.Value ?? 0;
            var cycleStart = plan.CycleStart.Value;
            var cycleEnd = cycleStart.AddMonths(11);

            var invoiceNo = NextInvoiceNo();
            var invTitle = $"{b.Title.Value}（{cycleStart:yyyy年M月}〜{cycleEnd:yyyy年M月}分・年額）";
            var inv = new Invoice();
            inv.InvoiceNo.Value = invoiceNo;
            inv.PartnerRef.Value = b.PartnerRef.Value;
            inv.ProjectRef.Value = b.ProjectRef.Value;
            inv.DepartmentRef.Value = b.DepartmentRef.Value;
            inv.Title.Value = invTitle;
            inv.IssueDate.Value = monthEnd;
            inv.DueDate.Value = dueDate;
            inv.Amount.Value = annual;
            inv.TaxAmount.Value = annualTax;
            inv.Status.Value = "issued";
            inv.InvoiceSource.Value = "recurring_annual";
            inv.RecurringBillingRef.Value = b.Id.Value;
            inv.BillingMonth.Value = cycleStart;
            inv.Lines.AddRows(1);
            foreach (var lineRow in inv.Lines.Rows)
            {
                var line = (InvoiceLine)lineRow;
                line.LineNo.Value = 1;
                line.Description.Value = invTitle;
                line.Qty.Value = 1;
                line.Unit.Value = "式";
                line.UnitPrice.Value = annual;
                line.Amount.Value = annual;
                line.TaxCategoryRef.Value = salesTaxCatId;
            }
            var retInv = inv.Submit();
            if (retInv != true)
            {
                Toaster.Error($"年額請求書の生成に失敗しました: {invTitle}");
                ResultLabel.Text = $"中断: 月額 {created}件 / 年額請求 {annualCreated}件 / 按分振替 {deferCreated}件";
                PlanLines.Reload();
                return;
            }
            var find = new ModuleSearcher<Invoice>();
            find.AddEquals(i => i.InvoiceNo.Value, invoiceNo);
            var createdInv = find.ExecuteFirstOrDefault();
            if (createdInv == null)
            {
                Toaster.Error($"生成した年額請求書が見つかりません: {invoiceNo}");
                PlanLines.Reload();
                return;
            }
            var annualInvId = ((Invoice)createdInv).Id.Value;

            // 前受計上仕訳: D 売掛金(税込) / C 前受収益(年額) + C 仮受消費税(税)
            var nextNo = NextJournalNo(typedFy.Id.Value);
            var lineCount = 2;
            if (annualTax > 0) { lineCount = 3; }
            var je = new JournalEntry();
            je.EntryDate.Value = monthEnd;
            je.EntryType.Value = "auto";
            je.Description.Value = $"SaaS年額 前受計上 {invTitle}";
            je.Status.Value = "posted";
            je.JournalNo.Value = nextNo;
            je.FiscalYearRef.Value = typedFy.Id.Value;
            je.SourceType.Value = "recurring_annual";
            je.SourceId.Value = annualInvId;
            je.Lines.AddRows(lineCount);
            var idxA = 0;
            foreach (var jlRow in je.Lines.Rows)
            {
                var l = (JournalLine)jlRow;
                idxA = idxA + 1;
                l.LineNo.Value = idxA;
                l.Description.Value = invTitle;
                l.TaxInputMode.Value = "none";
                if (b.ProjectRef.Value != null) { l.ProjectRef.Value = b.ProjectRef.Value; }
                if (b.DepartmentRef.Value != null) { l.Department.Value = b.DepartmentRef.Value; }
                if (idxA == 1)
                {
                    l.Dc.Value = "D";
                    l.Account.Value = arAccountId;
                    l.Amount.Value = annualGross;
                    l.InputAmount.Value = annualGross;
                }
                else if (idxA == 2)
                {
                    l.Dc.Value = "C";
                    l.Account.Value = deferredAccountId;
                    l.TaxCategory.Value = salesTaxCatId;
                    l.Amount.Value = annual;
                    l.InputAmount.Value = annual;
                }
                else
                {
                    l.Dc.Value = "C";
                    l.Account.Value = taxAccountId;
                    l.TaxCategory.Value = salesTaxCatId;
                    l.IsTaxLine.Value = true;
                    l.ParentLineNo.Value = 2;
                    l.Amount.Value = annualTax;
                    l.InputAmount.Value = annualTax;
                    l.Description.Value = "消費税（行2）";
                }
            }
            je.MarkRemainingLinesOutOfScope();
            je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
            var retJe = je.Submit();
            if (retJe != true)
            {
                Toaster.Error($"前受計上仕訳の生成に失敗しました: {invTitle}（請求書 {invoiceNo} は作成済み）");
                ResultLabel.Text = $"中断: 月額 {created}件 / 年額請求 {annualCreated}件 / 按分振替 {deferCreated}件";
                PlanLines.Reload();
                return;
            }
            journalNos.Add($"No.{nextNo}");
            annualCreated = annualCreated + 1;
            CreatePendingReceiptFor(annualInvId, invoiceNo);

            // 当月分（周期1ヶ月目）の按分振替
            var deferNo = CreateDeferJournal(typedFy.Id.Value, b, annualInvId, portion, monthFirst, monthEnd);
            if (deferNo == 0)
            {
                ResultLabel.Text = $"中断: 月額 {created}件 / 年額請求 {annualCreated}件 / 按分振替 {deferCreated}件";
                PlanLines.Reload();
                return;
            }
            journalNos.Add($"No.{deferNo}");
            deferCreated = deferCreated + 1;

            plan.Status.Value = "done";
            plan.InvoiceNo.Value = invoiceNo;
            plan.Detail.Value = $"年額請求書＋前受計上（No.{nextNo}）＋按分振替（No.{deferNo}）を生成済み";
            plan.Submit();
            continue;
        }

        // ===== 年払い（周期途中月）: 按分振替のみ =====
        if (plan.PlanKind.Value == "defer")
        {
            int portion = plan.DeferAmount.Value ?? 0;
            var annualInvId = plan.AnnualInvoiceId.Value;
            var deferNo = CreateDeferJournal(typedFy.Id.Value, b, annualInvId, portion, monthFirst, monthEnd);
            if (deferNo == 0)
            {
                ResultLabel.Text = $"中断: 月額 {created}件 / 年額請求 {annualCreated}件 / 按分振替 {deferCreated}件";
                PlanLines.Reload();
                return;
            }
            journalNos.Add($"No.{deferNo}");
            deferCreated = deferCreated + 1;

            var monthNo = (plan.CycleIndex.Value ?? 0) + 1;
            plan.Status.Value = "done";
            plan.Detail.Value = $"按分振替（{monthNo}/12ヶ月目・No.{deferNo}）を生成済み";
            plan.Submit();
            continue;
        }
    }

    var summary = $"{monthFirst:yyyy年M月}分: 月額請求 {created}件 / 年額請求 {annualCreated}件 / 按分振替 {deferCreated}件 / 対象外 {excludedCount}件";
    if (journalNos.Count > 0)
    {
        summary = summary + $" / 仕訳 {string.Join(", ", journalNos)}";
    }
    ResultLabel.Text = summary;
    PlanLines.Reload();
    var totalCreated = created + annualCreated + deferCreated;
    if (totalCreated > 0)
    {
        Toaster.Success($"定期請求の処理 {totalCreated} 件を生成しました（月額 {created} / 年額 {annualCreated} / 振替 {deferCreated}）");
    }
    else
    {
        Toaster.Info("生成対象はありませんでした");
    }
}

// 按分振替仕訳 (D 前受収益 / C SaaS売上高。税行なし) を生成し、仕訳番号を返す（失敗時 0）
int CreateDeferJournal(object fiscalYearId, object billing, object annualInvId, int portion, DateOnly monthFirst, DateOnly monthEnd)
{
    var b = (RecurringBilling)billing;

    // 科目の解決（前受収益2110 / SaaS売上高4020）
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "4020", "2110");
    var accounts = accS.Execute();
    object saasAccountId = null;
    object deferredAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "4020") { saasAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2110") { deferredAccountId = acc.Id.Value; }
    }
    if (saasAccountId == null || deferredAccountId == null)
    {
        Toaster.Error("按分振替に必要な科目（2110/4020）がありません");
        return 0;
    }

    var deferNo = NextJournalNo(fiscalYearId);
    var dje = new JournalEntry();
    dje.EntryDate.Value = monthEnd;
    dje.EntryType.Value = "auto";
    dje.Description.Value = $"前受収益の按分振替 {b.Title.Value}（{monthFirst:yyyy年M月}分）";
    dje.Status.Value = "posted";
    dje.JournalNo.Value = deferNo;
    dje.FiscalYearRef.Value = fiscalYearId;
    dje.SourceType.Value = "recurring_defer";
    dje.SourceId.Value = annualInvId;
    dje.Lines.AddRows(2);
    var idxD = 0;
    foreach (var jlRow in dje.Lines.Rows)
    {
        var l = (JournalLine)jlRow;
        idxD = idxD + 1;
        l.LineNo.Value = idxD;
        l.Description.Value = $"{b.Title.Value}（{monthFirst:yyyy年M月}分）";
        l.TaxInputMode.Value = "none";
        if (b.ProjectRef.Value != null) { l.ProjectRef.Value = b.ProjectRef.Value; }
        if (b.DepartmentRef.Value != null) { l.Department.Value = b.DepartmentRef.Value; }
        if (idxD == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = deferredAccountId;
            l.Amount.Value = portion;
            l.InputAmount.Value = portion;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = saasAccountId;
            l.Amount.Value = portion;
            l.InputAmount.Value = portion;
        }
    }
    // 前受収益の按分振替は内部振替なので全明細を「対象外」に上書きする（ADR-0053）。
    // 課税売上は年額請求の時点で計上済みで、科目の既定に任せると月次の振替で二重計上になる。
    dje.MarkAllLinesOutOfScope();
    dje.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var retDje = dje.Submit();
    if (retDje != true)
    {
        Toaster.Error($"按分振替仕訳の生成に失敗しました: {b.Title.Value}");
        return 0;
    }
    return deferNo;
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
