// BankPosting.mod.cs — 一括起票（v3 新設。ISSUE-0003）
// 責務: 未起票明細への相手科目の確定（ルール再適用・AI推定・手動）と「対象外」の分別、
//        候補科目での一括起票（自動仕訳）。ルール/AI はメモリのみ（保存・起票で確定）。
// 仕訳生成の規律は旧 BankImport.PostAll を移設（締め済みガード・二重起票ガード・税行方式・日付順採番）。

void Detail_OnAfterInit()
{
    ResultLabel.Text = "";
    // 初期表示でサマリを出すため、リストのロードを初期化中に明示的に行う
    // （自動ロードの OnDataChanged はレンダリング通知を伴わず、ラベルが初回描画に乗らない——実測）
    PendingList.Reload();
    UpdateSummary();
}

void PendingList_OnDataChanged()
{
    UpdateSummary();
}

void UpdateSummary()
{
    // **「起票対象」は `PostAll` が実際に起票できる行だけを数える**（BUG-0223）。
    // 旧実装は「相手科目の候補が入っているか」しか見ておらず、
    // 損益科目の行に部門が要る（ADR-0056）という起票の必須条件を数えていなかった。
    // その結果「未起票 3 件／起票対象 3 件」と出ている状態で起票すると
    // 「0 件起票／部門未選択でスキップ 3 件」になる——**同じ判定を 2 通りに実装していた**。
    // 部門が要るかどうかは相手科目の種別で決まるので、ここでも科目マスタを引く（画面表示なので毎回でよい）。
    var deptRequired = DeptRequiredAccountIds();

    var total = 0;
    var ready = 0;
    var unset = 0;
    var needDept = 0;
    var marked = 0;
    foreach (var r in PendingList.Rows)
    {
        var t = (BankStatementLine)r;
        total = total + 1;
        if (t.MarkExcluded.Value == true) { marked = marked + 1; continue; }
        if (t.SuggestedAccount.Value == null) { unset = unset + 1; continue; }
        if (deptRequired.Contains($"{t.SuggestedAccount.Value}") && t.DepartmentRef.Value == null)
        {
            needDept = needDept + 1;
            continue;
        }
        ready = ready + 1;
    }
    var deptText = "";
    if (needDept > 0) { deptText = $" ／ 部門未選択で起票できない {needDept} 件"; }
    SummaryLabel.Text = $"未起票 {total} 件 ／ 起票対象（起票できる） {ready} 件 ／ 候補未設定 {unset} 件{deptText} ／ 対象外チェック中 {marked} 件";
}

// 部門が必須な科目（＝損益科目・ADR-0056）の id を文字列で集める。
// `PostAll` の判定（`counterType == "expense" || counterType == "revenue"`）と同じ式にしてある。
// **`List<object>` を引数で持ち回らない**——`Execute()` の戻り値をそのまま渡すと
// スクリプトが黙って例外になり、サマリのラベルが空のまま描画された（2026-08-19 実測）
List<string> DeptRequiredAccountIds()
{
    var ids = new List<string>();
    var accS = new ModuleSearcher<Account>();
    foreach (var a in accS.Execute())
    {
        var acc = (Account)a;
        var ty = acc.AccountType.Value;
        if (ty == "expense" || ty == "revenue") { ids.Add($"{acc.Id.Value}"); }
    }
    return ids;
}

// ============ 保存（候補の確定＋対象外の分別） ============

void Save_OnClick()
{
    using var loading = LoadingService.StartLoading(0);
    SaveListEdits();
    PendingList.Reload();
    UpdateSummary();
    Toaster.Success("変更を保存しました（対象外チェックの行は一覧から外れます）");
}

// 候補の手修正を保存し、「対象外」チェックの行を excluded へ遷移する（保存した瞬間に一覧から消える）
void SaveListEdits()
{
    foreach (var r in PendingList.Rows)
    {
        var t = (BankStatementLine)r;
        if (t.MarkExcluded.Value == true)
        {
            t.Status.Value = "excluded";
            t.MarkExcluded.Value = false;
        }
        t.Submit();
    }
}

// ============ ルールを適用（メモリのみ。保存または一括起票で確定） ============

void ApplyRules_OnClick()
{
    var rs = new ModuleSearcher<MatchingRule>();
    rs.AddEquals(r => r.IsActive.Value, true);
    rs.OrderBy(r => r.Priority.Value);
    var rules = rs.Execute();
    if (rules.Count == 0) { Toaster.Info("有効な仕訳ルールがありません"); return; }

    var applied = 0;
    foreach (var r in PendingList.Rows)
    {
        var t = (BankStatementLine)r;
        if (t.MarkExcluded.Value == true) continue;
        if (t.SuggestedAccount.Value != null) continue;  // 手入力・適用済みは上書きしない
        var desc = t.Description.Value ?? "";
        var outAmt = t.AmountOut.Value ?? 0;
        var inAmt = t.AmountIn.Value ?? 0;
        foreach (var rm in rules)
        {
            var rule = (MatchingRule)rm;
            var kw = rule.Keyword.Value;
            if (kw == null || kw == "") continue;
            var dir = rule.Direction.Value;
            if (dir == "in" && inAmt <= 0) continue;
            if (dir == "out" && outAmt <= 0) continue;
            if (!desc.Contains(kw)) continue;
            t.SuggestedAccount.Value = rule.Account.Value;
            // ルールが部門を持っていれば一緒に入れる（ADR-0056）。
            // 「AWS 利用料 → 通信費・開発1部」のように、部門まで機械的に決まる取引を手入力から外す
            if (rule.DepartmentRef.Value != null) { t.DepartmentRef.Value = rule.DepartmentRef.Value; }
            t.SuggestionSource.Value = "rule";
            applied = applied + 1;
            break;
        }
    }
    UpdateSummary();
    ResultLabel.Text = $"ルール適用: {applied} 件（保存または一括起票で確定されます）";
    if (applied > 0) Toaster.Success($"ルールで {applied} 件の相手科目を設定しました");
    else Toaster.Info("ルールに一致する明細がありませんでした");
}

// ============ AI 推定（メモリのみ。保存または一括起票で確定） ============

void AiSuggest_OnClick()
{
    using var loading = LoadingService.StartLoading(0);

    var targets = new List<BankStatementLine>();
    foreach (var r in PendingList.Rows)
    {
        var t = (BankStatementLine)r;
        if (t.MarkExcluded.Value == true) continue;
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
                applied = applied + 1;
                break;
            }
            break;
        }
    }
    UpdateSummary();
    // 「（モック応答）」は AI プロバイダが Mock のときだけ付く印。
    // 判定は文字列化して比べる（この repo の定石。動的型の生の `==` は型が違うと黙って false になる）。
    // **この開発機は User Secrets で AzureOpenAI に切り替わっている**ので、通常は印が出ない＝実 API を呼んでいる。
    var mockNote = "";
    var isMock = $"{data.isMock}";
    if (isMock == "True" || isMock == "true") mockNote = "（モック応答）";
    ResultLabel.Text = $"AI 推定: 対象 {targets.Count} 件 / 反映 {applied} 件{mockNote}（保存または一括起票で確定されます）";
    Toaster.Success($"AI が {applied} 件の相手科目を推定しました{mockNote}");
}

// ============ 一括起票 ============

void PostAll_OnClick()
{
    // **確認より先に保存してはいけない**（BUG-0163 / ADR-0062）。
    // 旧実装は `SaveListEdits()` を確認ダイアログの前に呼んでおり、
    // 件数を見て「キャンセル」を押しても候補科目の確定と `対象外 → excluded` の遷移は
    // すでに DB に入っていた（チェックした行は一覧から消えたまま戻らない）。
    // 件数は**画面のメモリ行**から数えれば保存しなくても分かる。
    // 順序は同じアプリの `BankImport.ConfirmImport_OnClick`（ダイアログ → 保存）に揃える。
    var plannedDeptRequired = DeptRequiredAccountIds();
    var plannedCount = 0;
    foreach (var r in PendingList.Rows)
    {
        var t = (BankStatementLine)r;
        if (t.MarkExcluded.Value == true) continue;   // これから「対象外」にする行は数えない
        if (t.SuggestedAccount.Value == null) continue;
        // サマリと同じ判定で数える（BUG-0223）。ダイアログの件数と結果が食い違わないようにする
        if (plannedDeptRequired.Contains($"{t.SuggestedAccount.Value}") && t.DepartmentRef.Value == null) continue;
        plannedCount = plannedCount + 1;
    }
    if (plannedCount == 0)
    {
        // なぜ 0 件なのかを言う（BUG-0223）。「候補が無い」と「部門が無い」では次の一手が違う
        var noDept = 0;
        foreach (var r in PendingList.Rows)
        {
            var t = (BankStatementLine)r;
            if (t.MarkExcluded.Value == true) continue;
            if (t.SuggestedAccount.Value == null) continue;
            if (plannedDeptRequired.Contains($"{t.SuggestedAccount.Value}") && t.DepartmentRef.Value == null) { noDept = noDept + 1; }
        }
        if (noDept > 0)
        {
            Toaster.Warn($"起票できる明細がありません（{noDept} 件は損益科目の行なのに部門が未選択です。部門を選んでから起票してください）");
        }
        else
        {
            Toaster.Info("起票対象（相手科目候補が設定済みの未起票明細）がありません");
        }
        return;
    }

    var answer = MessageBox.Show($"{plannedCount} 件の明細を仕訳として一括起票します。よろしいですか？", "起票する", "キャンセル");
    if (answer != "起票する") return;

    // ここから先は確定操作。画面の編集（候補・対象外チェック）を反映してから起票する
    SaveListEdits();

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
    if (targets.Count == 0)
    {
        PendingList.Reload();
        UpdateSummary();
        Toaster.Info("起票対象（相手科目候補が設定済みの未起票明細）がありません");
        return;
    }

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
    var skippedNoDept = 0;
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

        // 年度・期間の解決は境界日の比較の罠を避けるため月初日で行う（Project.md 知見）
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

        // 損益科目の行には部門が要る（ADR-0056）。人が明細を見て起票する経路なので、
        // 全社共通で黙って埋めずに**その明細だけスキップ**して部門を選ばせる
        var counterType = counter.AccountType.Value;
        if ((counterType == "expense" || counterType == "revenue") && t.DepartmentRef.Value == null)
        {
            skippedNoDept = skippedNoDept + 1;
            continue;
        }

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

        // 伝票採番（年度内連番。正典: JournalEntry.NextJournalNo。BUG-0069 で一本化）
        var nextNo = new JournalEntry().NextJournalNo(typedFy.Id.Value);

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
            // 明細で選んだ部門は**相手科目の行だけ**に持ち込む（ADR-0056・BUG-0347）。
            // 口座（普通預金）の行は「どの部門の預金か」という意味を持たないので全社共通に寄せる
            // ——ここに部門を付けると、資産科目を部門で絞った元帳が部門ごとにバラけてしまう。
            // 税行は FillMissingDepartments が ParentLineNo をたどって相手科目行から継ぐ。
            // 正典は入出金起票（CashEntry.mod.cs）の同じ処理
            if (isOut)
            {
                if (idx == 1)
                {
                    l.Dc.Value = "D";
                    l.Account.Value = counter.Id.Value;
                    if (t.DepartmentRef.Value != null) { l.Department.Value = t.DepartmentRef.Value; }
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
                    if (t.DepartmentRef.Value != null) { l.Department.Value = t.DepartmentRef.Value; }
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
        je.MarkRemainingLinesOutOfScope();
        je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
        // 貸借一致の検証（BUG-0068）。**Submit の前**に見るので、止めれば伝票は生まれない
        var imbalance = je.ValidateBalanced();
        if (imbalance != "")
        {
            Toaster.Error($"起票を中止した明細があります（{imbalance}）");
            failed = failed + 1;
            continue;
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

    PendingList.Reload();
    UpdateSummary();
    var nosText = "";
    if (postedNos.Count > 0) { nosText = $"（伝票 No.{string.Join(", ", postedNos)}）"; }
    var deptText = "";
    if (skippedNoDept > 0) { deptText = $" / 部門未選択でスキップ {skippedNoDept} 件"; }
    ResultLabel.Text = $"一括起票: {posted} 件起票{nosText} / 締め済み等スキップ {skippedClosed} 件{deptText} / 失敗 {failed} 件";
    if (posted > 0) Toaster.Success($"{posted} 件を仕訳として起票しました{nosText}");
    else Toaster.Warn("起票できた明細がありませんでした");
    if (skippedNoDept > 0) { Toaster.Warn($"{skippedNoDept} 件は部門が未選択のため起票していません（損益科目の行には部門が要ります）"); }
}
