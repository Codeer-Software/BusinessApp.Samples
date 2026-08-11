// JournalEntry.mod.cs — 振替伝票
// 責務: 会計年度の自動解決 / 締め済み期間ガード / 貸借合計のリアルタイム表示 /
//        保存時の消費税行の自動生成（税抜経理・インボイス経過措置対応）/
//        確定時の貸借一致チェックと年度内連番の採番
// 設計: docs/04_会計ドメイン設計.md §3 / docs/decisions/0002・0003

bool inLinesHandler = false;

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        EntryDate.Value = DateOnly.FromDateTime(DateTime.Today);
        EntryType.Value = "transfer";
        Status.Value = "draft";
        ResolveFiscalYear();
    }
    // 確定済みの案内（編集不可の理由と赤黒訂正への誘導。驚き最小: 2026-08-03 UXレビュー）
    PostedNote.IsVisible = !this.IsNewData && Status.Value == "posted";
    if (!this.IsNewData && Status.Value == "posted")
    {
        // 確定済み伝票は閲覧専用（訂正は赤黒訂正で行う）
        this.IsViewOnly = true;
        SaveDraftButton.IsVisible = false;
        PostButton.IsVisible = false;
    }
    // 削除は「保存済みの下書き」だけ（確定済み伝票は削除不可＝赤黒訂正で消し込む。ADR-0026）
    DeleteDraftButton.IsVisible = !this.IsNewData && (Status.Value == "draft");
    if (!this.IsNewData && Status.Value == "draft" && SourceType.Value != "import")
    {
        // 旧仕様の下書き（税抜変換済み・税行あり）を入力状態へ戻す。
        // 現仕様の下書きは生の入力のまま保存されるので通常は no-op。
        // CSV インポート由来（source_type='import'）は税行込みの生データが正なので畳まない。
        inLinesHandler = true;
        RestoreInputState();
        inLinesHandler = false;
    }
    UpdateTotals();
}

void EntryDate_OnDataChanged()
{
    ResolveFiscalYear();
}

void ResolveFiscalYear()
{
    if (EntryDate.Value == null)
    {
        FiscalYearRef.Value = null;
        return;
    }
    // 月初日で解決（月末日など境界日は日付書式の辞書順比較で一致に失敗する罠がある。Project.md 知見）
    var firstDay = new DateTime(EntryDate.Value.Year, EntryDate.Value.Month, 1);
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var fy = s.ExecuteFirstOrDefault();
    if (fy == null)
    {
        FiscalYearRef.Value = null;
        return;
    }
    var typed = (FiscalYear)fy;
    FiscalYearRef.Value = typed.Id.Value;
}

void Lines_OnDataChanged()
{
    if (inLinesHandler) return;
    inLinesHandler = true;
    ApplyLineDefaults();
    UpdateTotals();
    inLinesHandler = false;
}

// 新規明細行への既定値: 貸借は借方、科目選択時に科目マスタの既定税区分と内税を設定
void ApplyLineDefaults()
{
    var missingAccountIds = new List<object>();
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.Dc.Value == null || l.Dc.Value == "")
        {
            l.Dc.Value = "D";
        }
        if (l.Account.Value != null && l.TaxCategory.Value == null)
        {
            missingAccountIds.Add(l.Account.Value);
        }
    }
    if (missingAccountIds.Count == 0) return;

    var s = new ModuleSearcher<Account>();
    s.AddIn(e => e.Id.Value, missingAccountIds);
    var accounts = s.Execute();
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.Account.Value == null) continue;
        if (l.TaxCategory.Value != null) continue;
        foreach (var a in accounts)
        {
            var acc = (Account)a;
            if (acc.Id.Value == l.Account.Value)
            {
                l.TaxCategory.Value = acc.DefaultTaxCategory.Value;
                if (l.TaxCategory.Value != null && (l.TaxInputMode.Value == null || l.TaxInputMode.Value == ""))
                {
                    l.TaxInputMode.Value = "inclusive";
                }
                break;
            }
        }
    }
}

// 税区分が入っていない明細を「対象外」にする（ADR-0052）。
// **スクリプトから仕訳を生成する経路は je.Submit() の直前に必ずこれを呼ぶこと**（例外なし）。
//
// 前提: 各経路は「税に意味のある行」——売上・費用の本体行と、それに対応する税行——に
// 必ず税区分を明示している。ここに残る行は相手勘定（売掛金・買掛金・預金・現金・未払金）か
// 内部振替の行であり、どちらも消費税の対象外。
//
// **当初は勘定科目マスタの既定で埋めたが、それは誤りだった**（2026-08-12 の実測で発見）:
//   ・減価償却の貸方（工具器具備品）に取得時の既定 PUR_10 が付き、課税仕入の「戻し」に化けて
//     課税仕入を 114,375 円過少にしていた
//   ・前受収益の按分振替の貸方（SaaS売上高）に SALES_10 が付き、請求時に計上済みの課税売上を
//     月次の振替でもう一度計上していた（二重計上）
// 科目の既定は「その科目の典型的な取引」には正しくても、**内部振替の行には正しくない**。
// 相手科目そのものが経済的実体になる画面（入出金起票）は、経路側で明示的に既定を入れている。
//
// 税行（IsTaxLine=true）は本体行と同じ税区分でなければならず、「対象外」を入れるとその税額が
// 消費税集計表から消える（B-5 の再発）ため、ここでは意図的に触らない。税区分の無い税行が
// 残れば DB の NOT NULL で落ちる＝呼び出し側のバグとして早期に表面化する。
void MarkRemainingLinesOutOfScope()
{
    var hasMissing = false;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.TaxCategory.Value == null) { hasMissing = true; break; }
    }
    if (!hasMissing) return;

    // 「対象外」はコードではなく課税種別で引く（コードが変わっても壊れないように）
    var s = new ModuleSearcher<TaxCategory>();
    s.AddEquals(e => e.TaxationType.Value, "out_of_scope");
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return;
    var outOfScopeId = ((TaxCategory)found).Id.Value;

    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.TaxCategory.Value != null) continue;
        l.TaxCategory.Value = outOfScopeId;
    }
}

void UpdateTotals()
{
    var d = 0;
    var c = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.Amount.Value == null) continue;
        if (l.Dc.Value == "D") { d += l.Amount.Value; }
        if (l.Dc.Value == "C") { c += l.Amount.Value; }
    }
    DebitTotal.Value = d;
    CreditTotal.Value = c;
    BalanceDiff.Value = d - c;
}

void SaveDraft_OnClick()
{
    SaveEntry(false);
}

void Post_OnClick()
{
    SaveEntry(true);
}

void SaveEntry(bool post)
{
    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    if (!this.ValidateInput())
    {
        Toaster.Error("入力エラーがあります。項目を確認してください。");
        return;
    }
    if (EntryDate.Value == null)
    {
        EntryDate.SetError("取引日を入力してください");
        return;
    }

    // 会計年度の解決と締め済み期間ガード
    ResolveFiscalYear();
    if (FiscalYearRef.Value == null)
    {
        EntryDate.SetError("取引日に対応する会計年度がありません");
        return;
    }
    var entryFirstDay = new DateTime(EntryDate.Value.Year, EntryDate.Value.Month, 1);
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, entryFirstDay);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, entryFirstDay);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null)
    {
        EntryDate.SetError("取引日に対応する月次期間がありません");
        return;
    }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed")
    {
        EntryDate.SetError("締め済みの期間には起票できません");
        return;
    }

    // 明細チェック（税行以外）
    var realCount = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        realCount = realCount + 1;
        if (l.Amount.Value == null || l.Amount.Value <= 0)
        {
            Toaster.Error("明細の金額は 1 円以上で入力してください");
            return;
        }
        if (l.Account.Value == null)
        {
            Toaster.Error("明細の勘定科目を選択してください");
            return;
        }
    }
    if (realCount == 0)
    {
        Toaster.Error("明細を 1 行以上入力してください");
        return;
    }

    // 行番号は保存経路によらず必ず振る（line_no は NOT NULL）
    var lineNo = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        lineNo = lineNo + 1;
        l.LineNo.Value = lineNo;
    }

    // 税行の生成（税込→税抜の変換）は確定時のみ行う。
    // 下書きは入力そのままで保存する——変換済みの Amount を再変換する二重税抜化を防ぐため
    // （下書き保存→確定、確定失敗→再確定 の順路で必ず踏む罠だった）。
    // CSV インポート由来の下書き（source_type='import'）は税行込みの生データを
    // 無加工のまま確定する（移行元と 1 円もズレない保証。税の再計算はしない）。
    var isRawImport = (SourceType.Value == "import");
    if (post)
    {
        if (!isRawImport)
        {
            inLinesHandler = true;
            RegenerateTaxLines();
            inLinesHandler = false;
        }
        UpdateTotals();

        if (DebitTotal.Value != CreditTotal.Value)
        {
            var diff = BalanceDiff.Value;
            if (!isRawImport)
            {
                // 変換を巻き戻して入力状態に復元（このまま再確定しても二重変換しない）
                inLinesHandler = true;
                RestoreInputState();
                inLinesHandler = false;
                UpdateTotals();
            }
            Toaster.Error($"貸借が一致していません（差額 {diff:#,0} 円）");
            return;
        }
        if (JournalNo.Value == null)
        {
            JournalNo.Value = NextJournalNo();
        }
        Status.Value = "posted";
    }

    var ret = this.Submit();
    if (ret == false)
    {
        Toaster.Error("保存に失敗しました");
        if (post)
        {
            Status.Value = "draft";
            if (!isRawImport)
            {
                inLinesHandler = true;
                RestoreInputState();
                inLinesHandler = false;
                UpdateTotals();
            }
        }
        return;
    }

    if (post)
    {
        Toaster.Success($"伝票 No.{JournalNo.Value} を確定しました");
        this.IsViewOnly = true;
        SaveDraftButton.IsVisible = false;
        PostButton.IsVisible = false;
    }
    else
    {
        Toaster.Success("下書きを保存しました");
    }
}

// 税行を取り除き、本体行の金額をユーザー入力額（InputAmount）に戻す。
// RegenerateTaxLines の逆操作: 確定失敗時・旧仕様下書きの読込時に呼び、二重税抜化を防ぐ。
void RestoreInputState()
{
    var taxRows = new List<object>();
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true)
        {
            taxRows.Add(row);
        }
    }
    foreach (var r in taxRows)
    {
        Lines.DeleteRow(r);
    }
    var no = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        no = no + 1;
        l.LineNo.Value = no;
        if (l.InputAmount.Value != null)
        {
            l.Amount.Value = l.InputAmount.Value;
        }
    }
}

// 消費税行の再生成（既存の税行を削除→本体行から再計算して追加）
// 税抜経理: 行の Amount を本体額に書き換え、控除可能な消費税を 仮払(1900)/仮受(2200) の行として追加。
// 免税事業者からの仕入（経過措置）は控除割合マスタ分のみ税行にし、残りは本体へ上乗せ。
void RegenerateTaxLines()
{
    // 1. 既存税行の削除
    var taxRows = new List<object>();
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true)
        {
            taxRows.Add(row);
        }
    }
    foreach (var r in taxRows)
    {
        Lines.DeleteRow(r);
    }

    // 2. 行番号の振り直し
    var no = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        no = no + 1;
        l.LineNo.Value = no;
    }

    // 3. 税マスタ・税科目を 1 往復で取得
    var catSearch = new ModuleSearcher<TaxCategory>();
    var rateSearch = new ModuleSearcher<TaxRate>();
    var accSearch = new ModuleSearcher<Account>();
    accSearch.AddIn(e => e.Code.Value, "1900", "2200");
    var batch = BatchSearcher.Execute(catSearch, rateSearch, accSearch);
    var cats = batch.GetAt(0);
    var rates = batch.GetAt(1);
    var taxAccounts = batch.GetAt(2);

    object purchaseTaxAccountId = null;
    object salesTaxAccountId = null;
    foreach (var a in taxAccounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "1900") { purchaseTaxAccountId = acc.Id.Value; }
        if (acc.Code.Value == "2200") { salesTaxAccountId = acc.Id.Value; }
    }

    // 4. 経過措置の控除割合（取引日で期間解決。期間外は 0%）
    decimal transitionRate = 0;
    var trFirstDay = new DateTime(EntryDate.Value.Year, EntryDate.Value.Month, 1);
    var trSearch = new ModuleSearcher<InvoiceTransitionRate>();
    trSearch.AddLessThanOrEqual(e => e.ValidFrom.Value, trFirstDay);
    trSearch.AddGreaterThanOrEqual(e => e.ValidTo.Value, trFirstDay);
    var tr = trSearch.ExecuteFirstOrDefault();
    if (tr != null)
    {
        var typedTr = (InvoiceTransitionRate)tr;
        transitionRate = typedTr.RatePercent.Value ?? 0;
    }

    // 5. 本体行ごとに税額計算（追加する税行の情報を先に集める）
    var parentNos = new List<int>();
    var taxAmounts = new List<int>();
    var taxDcs = new List<string>();
    var taxAccountIds = new List<object>();
    var taxCatIds = new List<object>();

    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;

        if (l.TaxCategory.Value == null || l.TaxInputMode.Value == "none" || l.TaxInputMode.Value == null || l.TaxInputMode.Value == "")
        {
            l.InputAmount.Value = l.Amount.Value;
            continue;
        }

        // 税区分から課税種別・税率・経過措置フラグを解決
        var taxType = "";
        decimal ratePercent = 0;
        var isTransition = false;
        foreach (var cItem in cats)
        {
            var cat = (TaxCategory)cItem;
            if (cat.Id.Value == l.TaxCategory.Value)
            {
                taxType = cat.TaxationType.Value;
                if (cat.UsesTransitionDeduction.Value == true) { isTransition = true; }
                if (cat.Rate.Value != null)
                {
                    foreach (var rItem in rates)
                    {
                        var rate = (TaxRate)rItem;
                        if (rate.Id.Value == cat.Rate.Value)
                        {
                            ratePercent = rate.RatePercent.Value ?? 0;
                            break;
                        }
                    }
                }
                break;
            }
        }

        if (ratePercent == 0 || (taxType != "taxable_sales" && taxType != "taxable_purchase"))
        {
            l.InputAmount.Value = l.Amount.Value;
            continue;
        }

        // 入力額を保持し、本体額と税額を計算（端数は切り捨て）
        int input = l.Amount.Value;
        l.InputAmount.Value = input;

        int fullTax = 0;
        if (l.TaxInputMode.Value == "inclusive")
        {
            fullTax = input * ratePercent / (100 + ratePercent);
        }
        else
        {
            fullTax = input * ratePercent / 100;
        }

        int deductible = fullTax;
        if (isTransition)
        {
            deductible = fullTax * transitionRate / 100;
        }

        int baseAmount = 0;
        if (l.TaxInputMode.Value == "inclusive")
        {
            baseAmount = input - deductible;
        }
        else
        {
            baseAmount = input + fullTax - deductible;
        }

        l.Amount.Value = baseAmount;

        if (deductible > 0)
        {
            parentNos.Add((int)(l.LineNo.Value ?? 0));
            taxAmounts.Add(deductible);
            taxDcs.Add(l.Dc.Value);
            taxCatIds.Add(l.TaxCategory.Value);
            if (taxType == "taxable_purchase")
            {
                taxAccountIds.Add(purchaseTaxAccountId);
            }
            else
            {
                taxAccountIds.Add(salesTaxAccountId);
            }
        }
    }

    // 6. 税行を追加
    if (parentNos.Count == 0) return;
    var startCount = Lines.Rows.Count;
    Lines.AddRows(parentNos.Count);
    var idx = 0;
    var rowIndex = 0;
    foreach (var row in Lines.Rows)
    {
        rowIndex = rowIndex + 1;
        if (rowIndex <= startCount) continue;
        var l = (JournalLine)row;
        l.IsTaxLine.Value = true;
        l.ParentLineNo.Value = parentNos[idx];
        l.LineNo.Value = startCount + idx + 1;
        l.Dc.Value = taxDcs[idx];
        l.Account.Value = taxAccountIds[idx];
        l.TaxCategory.Value = taxCatIds[idx];
        l.TaxInputMode.Value = "none";
        l.Amount.Value = taxAmounts[idx];
        l.InputAmount.Value = taxAmounts[idx];
        l.Description.Value = $"消費税（行{parentNos[idx]}）";
        idx = idx + 1;
    }
}

int NextJournalNo()
{
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.FiscalYearRef.Value, FiscalYearRef.Value);
    s.OrderByDescending(e => e.JournalNo.Value);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    if (last == null) { return 1; }
    var typedLast = (JournalEntry)last;
    if (typedLast.JournalNo.Value == null) { return 1; }
    return (int)typedLast.JournalNo.Value + 1;
}

// 下書き伝票の削除（確定済みは削除不可＝赤黒訂正で対応。ADR-0026）
void DeleteDraft_OnClick()
{
    if (this.IsNewData) { Toaster.Error("保存されていない伝票です"); return; }
    if (Status.Value != "draft") { Toaster.Error("下書きの伝票のみ削除できます（確定済みは赤黒訂正で対応してください）"); return; }
    var result = MessageBox.Show("この下書き伝票を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;
    using var loading = LoadingService.StartLoading(0);
    this.Delete();
    Toaster.Success("下書き伝票を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("JournalEntry"));
}
