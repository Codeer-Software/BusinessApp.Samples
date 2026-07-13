// BankImport.mod.cs — 銀行・カード明細取込（D-2 / ADR-0012 ステージング＋仕訳リンク方式）
// 貼り付け CSV → bank_statement_lines へステージング（重複取込防止）
// → マッチングルール（マスタ）で相手科目候補 → ルール未該当は AI 推定（/api/bank_ai/suggest）
// → 候補科目で一括起票（税行方式・締め済みガード・伝票採番は ExpenseRequest と同じ規律）

void Detail_OnAfterInit()
{
    ResultLabel.Text = "";
}

// ============ 取込 ============

void Import_OnClick()
{
    if (BankAccountSel.Value == null) { Toaster.Error("取込先の口座を選択してください"); return; }
    var raw = CsvText.Value;
    if (raw == null || raw.Trim() == "") { Toaster.Error("明細CSVを貼り付けてください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 既存の重複キー（選択口座分）
    var existKeys = new List<string>();
    var es = new ModuleSearcher<BankStatementLine>();
    es.AddEquals(e => e.BankAccount.Value, BankAccountSel.Value);
    foreach (var em in es.Execute())
    {
        var ex = (BankStatementLine)em;
        if (ex.DedupKey.Value != null) existKeys.Add(ex.DedupKey.Value);
    }

    // 有効なマッチングルール（優先度順）
    var rs = new ModuleSearcher<MatchingRule>();
    rs.AddEquals(r => r.IsActive.Value, true);
    rs.OrderBy(r => r.Priority.Value);
    var rules = rs.Execute();

    var added = 0;
    var skipped = 0;
    var ruled = 0;
    var badLines = 0;
    var pasteBaseKeys = new List<string>();

    var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");
    var lines = text.Split('\n');
    foreach (var line in lines)
    {
        var t = line.Trim();
        if (t == "") continue;
        var cols = SplitCsvLine(t);
        if (cols.Count < 2) { badLines = badLines + 1; continue; }
        var dateStr = NormalizeDate(cols[0]);
        if (dateStr == "") { badLines = badLines + 1; continue; }  // ヘッダ行・日付でない行はスキップ
        var desc = cols[1];
        var outAmt = 0;
        var inAmt = 0;
        if (cols.Count > 2) outAmt = ParseAmount(cols[2]);
        if (cols.Count > 3) inAmt = ParseAmount(cols[3]);
        var hasBal = false;
        var bal = 0;
        if (cols.Count > 4 && cols[4].Trim() != "" && cols[4].Trim() != "\"\"")
        {
            bal = ParseAmount(cols[4]);
            hasBal = true;
        }
        if (outAmt <= 0 && inAmt <= 0) { badLines = badLines + 1; continue; }

        // 同一内容の明細は同一貼り付け内の出現順で連番を振って一意化
        // （銀行 CSV は過去分を含めて再出力されるため、キー一致＝取込済みとしてスキップできる）
        var baseKey = $"{dateStr}|{desc}|{outAmt}|{inAmt}";
        var seq = 0;
        foreach (var k in pasteBaseKeys) { if (k == baseKey) seq = seq + 1; }
        pasteBaseKeys.Add(baseKey);
        var key = $"{baseKey}|{seq}";
        if (existKeys.Contains(key)) { skipped = skipped + 1; continue; }

        // マッチングルール適用（優先度順・最初の一致）
        object suggested = null;
        foreach (var rm in rules)
        {
            var rule = (MatchingRule)rm;
            var kw = rule.Keyword.Value;
            if (kw == null || kw == "") continue;
            var dir = rule.Direction.Value;
            if (dir == "in" && inAmt <= 0) continue;
            if (dir == "out" && outAmt <= 0) continue;
            if (!desc.Contains(kw)) continue;
            suggested = rule.Account.Value;
            break;
        }

        var row = new BankStatementLine();
        row.BankAccount.Value = BankAccountSel.Value;
        row.LineDate.Value = DateOnly.FromDateTime(ToDate(dateStr));
        row.Description.Value = desc;
        row.AmountOut.Value = outAmt;
        row.AmountIn.Value = inAmt;
        if (hasBal) row.Balance.Value = bal;
        row.DedupKey.Value = key;
        row.Status.Value = "preview";
        if (suggested != null)
        {
            row.SuggestedAccount.Value = suggested;
            row.SuggestionSource.Value = "rule";
            ruled = ruled + 1;
        }
        row.ImportedAt.Value = DateTime.Now;
        // 表示専用モジュールの this.Submit() は機能しないため、行単位で直接保存する
        var ok = row.Submit();
        if (ok == true) { added = added + 1; }
        else { badLines = badLines + 1; }
    }

    if (added == 0)
    {
        ResultLabel.Text = $"プレビュー 0 件（重複スキップ {skipped} 件 / スキップ行（ヘッダ・明細以外） {badLines} 行）";
        Toaster.Warn("取り込める明細がありませんでした");
        return;
    }
    PreviewLines.Reload();
    ResultLabel.Text = $"プレビュー {added} 件 / ルール適用 {ruled} 件 / 重複スキップ {skipped} 件 / スキップ行（ヘッダ・明細以外） {badLines} 行 — 内容を確認して「この内容で登録」を押してください";
    Toaster.Success($"{added} 件をプレビューに読み込みました。内容を確認して「この内容で登録」を押してください");
}

// プレビューの確定: preview → pending（未起票の明細として正式登録）
void ConfirmImport_OnClick()
{
    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var s = new ModuleSearcher<BankStatementLine>();
    s.AddEquals(e => e.Status.Value, "preview");
    var rows = s.Execute();
    if (rows.Count == 0) { Toaster.Info("プレビュー中の明細がありません"); return; }

    var done = 0;
    foreach (var m in rows)
    {
        var r = (BankStatementLine)m;
        r.Status.Value = "pending";
        var ok = r.Submit();
        if (ok == true) { done = done + 1; }
    }
    PreviewLines.Reload();
    PendingLines.Reload();
    ResultLabel.Text = $"{done} 件を登録しました（未起票の明細へ移動）";
    Toaster.Success($"{done} 件の明細を登録しました");
}

// プレビューの取り消し: preview 行を削除（重複キーも解放され、同じ CSV を再取込できる）
void CancelImport_OnClick()
{
    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var s = new ModuleSearcher<BankStatementLine>();
    s.AddEquals(e => e.Status.Value, "preview");
    var rows = s.Execute();
    if (rows.Count == 0) { Toaster.Info("プレビュー中の明細がありません"); return; }

    var done = 0;
    foreach (var m in rows)
    {
        var r = (BankStatementLine)m;
        var ok = r.Delete();
        if (ok == true) { done = done + 1; }
    }
    PreviewLines.Reload();
    ResultLabel.Text = $"プレビュー {done} 件を取り消しました";
    Toaster.Info($"プレビュー {done} 件を取り消しました（同じ CSV を貼り直せます）");
}

// ============ AI 科目推定（ルール未該当分） ============

void AiSuggest_OnClick()
{
    using var loading = LoadingService.StartLoading(0);
    SaveListEdits();  // 画面上の編集（候補の手修正）を先に保存

    var ls = new ModuleSearcher<BankStatementLine>();
    ls.AddEquals(e => e.Status.Value, "pending");
    var all = ls.Execute();
    var targets = new List<BankStatementLine>();
    foreach (var m in all)
    {
        var t = (BankStatementLine)m;
        if (t.SuggestedAccount.Value == null) targets.Add(t);
    }
    if (targets.Count == 0) { Toaster.Info("AI 推定の対象（相手科目候補が未設定の明細）がありません"); return; }

    var accS = new ModuleSearcher<Account>();
    accS.OrderBy(a => a.Code.Value);
    var accounts = accS.Execute();
    var candText = "";
    foreach (var am in accounts)
    {
        var a = (Account)am;
        candText = candText + $"{a.Code.Value} {a.Name.Value}\n";
    }
    var lineText = "";
    foreach (var t in targets)
    {
        var d = t.Description.Value ?? "";
        d = d.Replace("|", " ").Replace("\n", " ");
        lineText = lineText + $"{t.Id.Value}|{d}|{t.AmountIn.Value ?? 0}|{t.AmountOut.Value ?? 0}\n";
    }

    var body = new JsonObject();
    body.Candidates = candText;
    body.Lines = lineText;
    var result = WebApiService.Post("/api/bank_ai/suggest", body);
    if (result.StatusCode != 200) { Toaster.Error("AI 推定サービスに接続できませんでした"); return; }
    var data = result.JsonObject;

    var applied = 0;
    foreach (var s in data.suggestions)
    {
        foreach (var t in targets)
        {
            if ($"{t.Id.Value}" != $"{s.id}") continue;
            foreach (var am in accounts)
            {
                var a = (Account)am;
                if (a.Code.Value != $"{s.code}") continue;
                t.SuggestedAccount.Value = a.Id.Value;
                t.SuggestionSource.Value = "ai";
                var ok = t.Submit();
                if (ok == true) applied = applied + 1;
                break;
            }
            break;
        }
    }
    PendingLines.Reload();
    var mockNote = "";
    if (data.isMock == true) mockNote = "（モック応答）";
    ResultLabel.Text = $"AI 推定: 対象 {targets.Count} 件 / 反映 {applied} 件{mockNote}";
    Toaster.Success($"AI が {applied} 件の相手科目を推定しました{mockNote}");
}

// ============ 一括起票 ============

void PostAll_OnClick()
{
    SaveListEdits();  // 画面上の編集（候補の手修正・対象外化）を先に保存

    var ls = new ModuleSearcher<BankStatementLine>();
    ls.AddEquals(e => e.Status.Value, "pending");
    ls.OrderBy(e => e.LineDate.Value);  // 伝票番号が日付順に並ぶように
    var all = ls.Execute();
    var targets = new List<BankStatementLine>();
    foreach (var m in all)
    {
        var t = (BankStatementLine)m;
        if (t.SuggestedAccount.Value != null) targets.Add(t);
    }
    if (targets.Count == 0) { Toaster.Info("起票対象（相手科目候補が設定済みの未起票明細）がありません"); return; }

    var answer = MessageBox.Show($"{targets.Count} 件の明細を仕訳として一括起票します。よろしいですか？", "起票する", "キャンセル");
    if (answer != "起票する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 参照マスタを一括ロード（ループ内 I/O の削減）
    var bs = new ModuleSearcher<BankAccount>();
    var bankAccounts = bs.Execute();
    var accS = new ModuleSearcher<Account>();
    var accounts = accS.Execute();
    var tcS = new ModuleSearcher<TaxCategory>();
    var taxCats = tcS.Execute();
    var trS = new ModuleSearcher<TaxRate>();
    var taxRates = trS.Execute();

    object purchaseTaxId = null;
    object salesTaxId = null;
    foreach (var am in accounts)
    {
        var a = (Account)am;
        if (a.Code.Value == "1900") purchaseTaxId = a.Id.Value;
        if (a.Code.Value == "2200") salesTaxId = a.Id.Value;
    }

    var posted = 0;
    var skippedClosed = 0;
    var failed = 0;
    var postedNos = new List<string>();
    foreach (var t in targets)
    {
        // 二重起票ガード（既に仕訳がある明細はリンクだけ張り直して起票済みへ）
        var dup = new ModuleSearcher<JournalEntry>();
        dup.AddEquals(e => e.SourceType.Value, "bank");
        dup.AddEquals(e => e.SourceId.Value, t.Id.Value);
        var dupHit = dup.ExecuteFirstOrDefault();
        if (dupHit != null)
        {
            t.JournalEntryId.Value = ((JournalEntry)dupHit).Id.Value;
            t.Status.Value = "journalized";
            t.Submit();
            continue;
        }

        // 口座の帳簿科目
        object ledgerId = null;
        foreach (var bm in bankAccounts)
        {
            var b = (BankAccount)bm;
            if ($"{b.Id.Value}" == $"{t.BankAccount.Value}") { ledgerId = b.LedgerAccount.Value; break; }
        }
        if (ledgerId == null) { failed = failed + 1; continue; }

        var d = t.LineDate.Value;
        if (d == null) { failed = failed + 1; continue; }

        // 年度・期間の解決は境界日の比較の罠を避けるため月初日で行う（Project.md 知見・RecurringRun 方式）
        var firstDay = new DateTime(d.Year, d.Month, 1);
        var ys = new ModuleSearcher<FiscalYear>();
        ys.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
        ys.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
        var fy = ys.ExecuteFirstOrDefault();
        if (fy == null) { skippedClosed = skippedClosed + 1; continue; }
        var typedFy = (FiscalYear)fy;
        var ps = new ModuleSearcher<FiscalPeriod>();
        ps.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
        ps.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
        var period = ps.ExecuteFirstOrDefault();
        if (period == null) { skippedClosed = skippedClosed + 1; continue; }
        if (((FiscalPeriod)period).Status.Value == "closed") { skippedClosed = skippedClosed + 1; continue; }

        // 相手科目
        Account counter = null;
        foreach (var am in accounts)
        {
            var a = (Account)am;
            if ($"{a.Id.Value}" == $"{t.SuggestedAccount.Value}") { counter = a; break; }
        }
        if (counter == null) { failed = failed + 1; continue; }

        var isOut = (t.AmountOut.Value ?? 0) > 0;
        var gross = isOut ? (t.AmountOut.Value ?? 0) : (t.AmountIn.Value ?? 0);
        if (gross <= 0) { failed = failed + 1; continue; }

        // 税額: 相手科目の既定税区分が課税（出金=課税仕入 / 入金=課税売上）のときのみ内税で算出
        var tax = 0;
        object taxCatId = counter.DefaultTaxCategory.Value;
        if (taxCatId != null)
        {
            foreach (var cm in taxCats)
            {
                var c = (TaxCategory)cm;
                if ($"{c.Id.Value}" != $"{taxCatId}") continue;
                var taxationType = c.TaxationType.Value ?? "";
                var taxable = false;
                if (isOut && taxationType == "taxable_purchase") taxable = true;
                if (!isOut && taxationType == "taxable_sales") taxable = true;
                if (taxable && c.Rate.Value != null)
                {
                    foreach (var rm2 in taxRates)
                    {
                        var r2 = (TaxRate)rm2;
                        if ($"{r2.Id.Value}" != $"{c.Rate.Value}") continue;
                        decimal pct = r2.RatePercent.Value ?? 0;
                        if (pct > 0) tax = gross * pct / (100 + pct);
                        break;
                    }
                }
                break;
            }
        }
        var baseAmount = gross - tax;

        // 伝票採番（年度内連番）
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

        // 仕訳生成（税行方式）
        // 出金: D 相手科目(本体) [+ D 仮払消費税] / C 口座科目(総額)
        // 入金: D 口座科目(総額) / C 相手科目(本体) [+ C 仮受消費税]
        var lineCount = (tax > 0) ? 3 : 2;
        var je = new JournalEntry();
        je.EntryDate.Value = d;
        je.EntryType.Value = "auto";
        je.Description.Value = $"銀行明細 {t.Description.Value}";
        je.Status.Value = "posted";
        je.JournalNo.Value = nextNo;
        je.FiscalYearRef.Value = typedFy.Id.Value;
        je.SourceType.Value = "bank";
        je.SourceId.Value = t.Id.Value;
        je.Lines.AddRows(lineCount);
        var idx = 0;
        foreach (var lr in je.Lines.Rows)
        {
            var l = (JournalLine)lr;
            idx = idx + 1;
            l.LineNo.Value = idx;
            l.Description.Value = t.Description.Value;
            if (isOut)
            {
                if (idx == 1)
                {
                    l.Dc.Value = "D";
                    l.Account.Value = counter.Id.Value;
                    if (taxCatId != null) l.TaxCategory.Value = taxCatId;
                    l.TaxInputMode.Value = (tax > 0) ? "inclusive" : "none";
                    l.Amount.Value = baseAmount;
                    l.InputAmount.Value = gross;
                }
                else if (idx == 2 && tax > 0)
                {
                    l.Dc.Value = "D";
                    l.Account.Value = purchaseTaxId;
                    l.TaxCategory.Value = taxCatId;
                    l.TaxInputMode.Value = "none";
                    l.IsTaxLine.Value = true;
                    l.ParentLineNo.Value = 1;
                    l.Amount.Value = tax;
                    l.InputAmount.Value = tax;
                    l.Description.Value = "消費税（行1）";
                }
                else
                {
                    l.Dc.Value = "C";
                    l.Account.Value = ledgerId;
                    l.TaxInputMode.Value = "none";
                    l.Amount.Value = gross;
                    l.InputAmount.Value = gross;
                }
            }
            else
            {
                if (idx == 1)
                {
                    l.Dc.Value = "D";
                    l.Account.Value = ledgerId;
                    l.TaxInputMode.Value = "none";
                    l.Amount.Value = gross;
                    l.InputAmount.Value = gross;
                }
                else if (idx == 2)
                {
                    l.Dc.Value = "C";
                    l.Account.Value = counter.Id.Value;
                    if (taxCatId != null) l.TaxCategory.Value = taxCatId;
                    l.TaxInputMode.Value = (tax > 0) ? "inclusive" : "none";
                    l.Amount.Value = baseAmount;
                    l.InputAmount.Value = gross;
                }
                else
                {
                    l.Dc.Value = "C";
                    l.Account.Value = salesTaxId;
                    l.TaxCategory.Value = taxCatId;
                    l.TaxInputMode.Value = "none";
                    l.IsTaxLine.Value = true;
                    l.ParentLineNo.Value = 2;
                    l.Amount.Value = tax;
                    l.InputAmount.Value = tax;
                    l.Description.Value = "消費税（行2）";
                }
            }
        }
        var ok = je.Submit();
        if (ok != true) { failed = failed + 1; continue; }

        // 生成仕訳の id を明細にリンク（je.Id は submit 後の取得を信用せず DB から引く）
        var js2 = new ModuleSearcher<JournalEntry>();
        js2.AddEquals(e => e.SourceType.Value, "bank");
        js2.AddEquals(e => e.SourceId.Value, t.Id.Value);
        var created = js2.ExecuteFirstOrDefault();
        if (created != null) { t.JournalEntryId.Value = ((JournalEntry)created).Id.Value; }
        t.Status.Value = "journalized";
        t.Submit();
        posted = posted + 1;
        postedNos.Add($"{nextNo}");
    }

    PendingLines.Reload();
    var nosText = "";
    if (postedNos.Count > 0) { nosText = $"（伝票 No.{string.Join(", ", postedNos)}）"; }
    ResultLabel.Text = $"一括起票: {posted} 件起票{nosText} / 締め済み等スキップ {skippedClosed} 件 / 失敗 {failed} 件";
    if (posted > 0) Toaster.Success($"{posted} 件を仕訳として起票しました{nosText}");
    else Toaster.Warn("起票できた明細がありませんでした");
}

// ============ 変更保存 ============

void Save_OnClick()
{
    using var loading = LoadingService.StartLoading(0);
    SaveListEdits();
    PendingLines.Reload();
    Toaster.Success("明細の変更を保存しました");
}

// 一覧内で編集された行を行単位で保存する（表示専用モジュールの this.Submit() は機能しないため）。
// 未変更行の Submit は「送信データなし（null）」で無害
void SaveListEdits()
{
    foreach (var r in PendingLines.Rows)
    {
        var line = (BankStatementLine)r;
        line.Submit();
    }
}

// ============ ヘルパ ============

// CSV 1行の分割。引用符内のカンマ（例: "1,234"）に対応するため、
// カンマで素朴に分割した後、引用符の数が奇数の断片を結合し直す方式
List<string> SplitCsvLine(string line)
{
    var parts = line.Split(',');
    var result = new List<string>();
    var buf = "";
    var inQuote = false;
    foreach (var part in parts)
    {
        var quotes = part.Split('"').Length - 1;
        if (inQuote)
        {
            buf = buf + "," + part;
            if (quotes % 2 == 1)
            {
                result.Add(CleanCsvCell(buf));
                inQuote = false;
            }
        }
        else
        {
            if (quotes % 2 == 1)
            {
                buf = part;
                inQuote = true;
            }
            else
            {
                result.Add(CleanCsvCell(part));
            }
        }
    }
    if (inQuote) result.Add(CleanCsvCell(buf));
    return result;
}

string CleanCsvCell(string s)
{
    var t = s.Trim();
    if (t.StartsWith("\"") && t.EndsWith("\"") && t.Length >= 2)
    {
        t = t.Substring(1, t.Length - 2);
    }
    return t.Replace("\"\"", "\"").Trim();
}

// 日付文字列の正規化。yyyy/M/d・yyyy-M-d・yyyy.M.d・和式に対応。日付でなければ "" を返す
string NormalizeDate(string s)
{
    var t = s.Trim().Replace("\"", "").Replace("-", "/").Replace(".", "/").Replace("年", "/").Replace("月", "/").Replace("日", "");
    var p = t.Split('/');
    if (p.Length != 3) return "";
    if (!IsNumeric(p[0].Trim()) || !IsNumeric(p[1].Trim()) || !IsNumeric(p[2].Trim())) return "";
    var y = int.Parse(p[0].Trim());
    var m = int.Parse(p[1].Trim());
    var dd = int.Parse(p[2].Trim());
    if (y < 1990 || y > 2100 || m < 1 || m > 12 || dd < 1 || dd > 31) return "";
    var dt = new DateTime(y, m, 1).AddDays(dd - 1);
    if (dt.Month != m) return "";  // 2/30 のような存在しない日付
    return $"{y}/{m}/{dd}";
}

DateTime ToDate(string ymd)
{
    var p = ymd.Split('/');
    var y = int.Parse(p[0]);
    var m = int.Parse(p[1]);
    var dd = int.Parse(p[2]);
    return new DateTime(y, m, 1).AddDays(dd - 1);
}

// 金額文字列 → 円整数。カンマ・引用符・通貨記号を除去。数値でなければ 0
int ParseAmount(string s)
{
    var t = s.Trim().Replace("\"", "").Replace(",", "").Replace("¥", "").Replace("\\", "").Replace("円", "").Replace(" ", "");
    if (t == "") return 0;
    var neg = false;
    if (t.StartsWith("-")) { neg = true; t = t.Substring(1); }
    if (!IsNumeric(t)) return 0;
    var v = int.Parse(t);
    if (neg) v = -v;
    return v;
}

bool IsNumeric(string s)
{
    if (s == null || s == "") return false;
    var digits = "0123456789";
    for (int i = 0; i < s.Length; i++)
    {
        if (!digits.Contains(s.Substring(i, 1))) return false;
    }
    return true;
}
