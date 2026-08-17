// BankImport.mod.cs — 銀行・カード明細取込（v3: 取込専用画面。ISSUE-0003）
// 責務: CSV貼り付け → プレビューテーブル (bank_statement_preview) へステージング →
//        受入修正（原本列の編集・行削除。保存で確定）→「この内容で登録」で本番
//        (bank_statement_lines, status='pending') へ移送＋仕訳ルール適用（Q-a 案1）。
// 科目の確定・対象外分別・起票は「一括起票」(BankPosting)、監査・例外操作は「明細一覧」で行う。
// 保存規律: リストの編集・行削除はメモリ上の変化。保存/登録ボタンで初めて DB に反映する
//          （スナップショット差分同期。DB全行との突き合わせはページング誤削除の恐れがあるため行わない）
// 注意: スナップショットは初期化・各操作後に**明示的に** CaptureSnapshot() で取る。
//       ListField の OnDataChanged は自動ロード時に発火しない（2026-07-22 実測。
//       イベント頼みだと「前回のプレビューが残った画面を開き直す→行削除→保存」で
//       スナップショットが空のまま＝削除が検出されないバグになった）

List<object> previewSnapshot = new List<object>();

// いまロードされている行の Id 集合を記録する（差分同期のスナップショット）。
// 保存時に「スナップショットにあるがメモリに無い行」＝ユーザーが削除した行として DELETE する
void CaptureSnapshot()
{
    previewSnapshot.Clear();
    foreach (var r in PreviewLines.Rows)
    {
        var p = (BankStatementPreview)r;
        previewSnapshot.Add(p.Id.Value);
    }
}

void Detail_OnAfterInit()
{
    ResultLabel.Text = "";

    // 取込先口座が1件しかなければ初期選択（2026-07-21 ユーザー要望）
    if (BankAccountSel.Value == null)
    {
        var bs = new ModuleSearcher<BankAccount>();
        bs.AddEquals(b => b.IsActive.Value, true);
        var accounts = bs.Execute();
        if (accounts.Count == 1)
        {
            BankAccountSel.Value = ((BankAccount)accounts[0]).Id.Value;
        }
    }

    // 初期表示分のスナップショットを確定させる（自動ロード待ちにせず明示ロード）
    PreviewLines.Reload();
    CaptureSnapshot();

    // 前回（または他の担当者）の未登録プレビューが残っている場合は警告する
    if (previewSnapshot.Count > 0)
    {
        ResultLabel.Text = $"⚠ 登録されていないプレビューが {previewSnapshot.Count} 件残っています（前回の作業の続き、または他の担当者の作業中かもしれません）。内容を確認して「この内容で登録」で確定するか、「全てのプレビューを取り消す」で破棄してから取込を始めてください";
    }
}

// ============ 取込 ============

void Import_OnClick()
{
    if (BankAccountSel.Value == null) { Toaster.Error("取込先の口座を選択してください"); return; }
    var raw = CsvText.Value;
    if (raw == null || raw.Trim() == "") { Toaster.Error("明細CSVを貼り付けてください"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 未保存のプレビュー編集があるかの検知は CLB API では困難なため、取込前に必ず暗黙保存する
    // （ISSUE-0003 §2.7 の決定に従う。編集が消える事故を構造的に防ぐ）
    SavePreviewEdits();

    // 既存の重複キー: 本番（選択口座分）∪ プレビュー（選択口座分）
    var existKeys = new List<string>();
    var es = new ModuleSearcher<BankStatementLine>();
    es.AddEquals(e => e.BankAccount.Value, BankAccountSel.Value);
    foreach (var em in es.Execute())
    {
        var ex = (BankStatementLine)em;
        if (ex.DedupKey.Value != null) existKeys.Add(ex.DedupKey.Value);
    }
    var eps = new ModuleSearcher<BankStatementPreview>();
    eps.AddEquals(e => e.BankAccount.Value, BankAccountSel.Value);
    foreach (var em in eps.Execute())
    {
        var ex = (BankStatementPreview)em;
        if (ex.DedupKey.Value != null) existKeys.Add(ex.DedupKey.Value);
    }

    var added = 0;
    var skipped = 0;
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
        var baseKey = $"{dateStr}|{desc}|{outAmt}|{inAmt}";
        var seq = 0;
        foreach (var k in pasteBaseKeys) { if (k == baseKey) seq = seq + 1; }
        pasteBaseKeys.Add(baseKey);
        var key = $"{baseKey}|{seq}";
        if (existKeys.Contains(key)) { skipped = skipped + 1; continue; }

        var row = new BankStatementPreview();
        row.BankAccount.Value = BankAccountSel.Value;
        row.LineDate.Value = DateOnly.FromDateTime(ToDate(dateStr));
        row.Description.Value = desc;
        row.AmountOut.Value = outAmt;
        row.AmountIn.Value = inAmt;
        if (hasBal) row.Balance.Value = bal;
        row.DedupKey.Value = key;
        row.ImportedAt.Value = DateTime.Now;
        var ok = row.Submit();
        if (ok == true) { added = added + 1; }
        else { badLines = badLines + 1; }
    }

    PreviewLines.Reload();
    CaptureSnapshot();
    if (added == 0)
    {
        ResultLabel.Text = $"プレビュー 0 件（重複スキップ {skipped} 件 / スキップ行（ヘッダ・明細以外） {badLines} 行）";
        Toaster.Warn("取り込める明細がありませんでした");
        return;
    }
    ResultLabel.Text = $"プレビュー {added} 件 / 重複スキップ {skipped} 件 / スキップ行（ヘッダ・明細以外） {badLines} 行 — 内容を確認して「この内容で登録」を押してください";
    Toaster.Success($"{added} 件をプレビューに読み込みました");
}

// ============ プレビューの保存（差分同期） ============

void SavePreview_OnClick()
{
    using var loading = LoadingService.StartLoading(0);
    SavePreviewEdits();
    Toaster.Success("プレビューの変更を保存しました");
}

// メモリ上の編集・行削除を DB に反映する。
// 1) スナップショットにあるがメモリに無い行 → DELETE（行削除の確定）
// 2) メモリに残る行 → 重複キーを再計算して Submit（原本列の修正はキーの元データのため）
void SavePreviewEdits()
{
    var currentIds = new List<string>();
    foreach (var r in PreviewLines.Rows)
    {
        var p = (BankStatementPreview)r;
        currentIds.Add($"{p.Id.Value}");
    }
    foreach (var idObj in previewSnapshot)
    {
        if (currentIds.Contains($"{idObj}")) continue;
        var ds = new ModuleSearcher<BankStatementPreview>();
        ds.AddEquals(p => p.Id.Value, idObj);
        var target = ds.ExecuteFirstOrDefault();
        if (target == null) continue;
        var typed = (BankStatementPreview)target;
        var okDel = typed.Delete();
        if (okDel != true) { Logger.Warn($"プレビュー行の削除に失敗しました (id={idObj})"); }
    }

    // DedupKey 末尾の連番は、**本番の既存明細から数え始める**。
    // プレビュー内だけで数え直すと、Import_OnClick が本番と突合して `…|1` を振った行を
    // ここで `…|0` に戻してしまい、直後の重複ガードが既存と誤判定して**無言で削除する**。
    // 本物の入出金が 1 件そのまま帳簿に載らず、残高照合の「差異」として遅れて表面化する（BUG-0162）。
    // 数える単位は口座ごと（口座をまたいで通し番号にすると、別口座に同じ明細が並んだだけで
    // キーがずれ、次回の再取込で重複と判定されなくなる＝逆方向の事故になる）
    var baseKeys = new List<string>();

    // プレビューに出ている口座の本番明細で先にシードする
    var seededAccounts = new List<string>();
    foreach (var r in PreviewLines.Rows)
    {
        var p0 = (BankStatementPreview)r;
        var accKey = $"{p0.BankAccount.Value}";
        if (seededAccounts.Contains(accKey)) continue;
        seededAccounts.Add(accKey);
        var ls = new ModuleSearcher<BankStatementLine>();
        ls.AddEquals(e => e.BankAccount.Value, p0.BankAccount.Value);
        foreach (var lm in ls.Execute())
        {
            var ex = (BankStatementLine)lm;
            var ed = ex.LineDate.Value;
            if (ed == null) continue;
            baseKeys.Add($"{accKey}#{ed.Year}/{ed.Month}/{ed.Day}|{ex.Description.Value}|{ex.AmountOut.Value ?? 0}|{ex.AmountIn.Value ?? 0}");
        }
    }

    foreach (var r in PreviewLines.Rows)
    {
        var p = (BankStatementPreview)r;
        var d = p.LineDate.Value;
        if (d != null)
        {
            var baseKey = $"{d.Year}/{d.Month}/{d.Day}|{p.Description.Value}|{p.AmountOut.Value ?? 0}|{p.AmountIn.Value ?? 0}";
            // 数えるときだけ口座を混ぜる。**保存する DedupKey は従来どおり口座なし**
            // （Import_OnClick 側が口座で絞ってから突合するので、既存データと互換が保たれる）
            var countKey = $"{p.BankAccount.Value}#{baseKey}";
            var seq = 0;
            foreach (var k in baseKeys) { if (k == countKey) seq = seq + 1; }
            baseKeys.Add(countKey);
            p.DedupKey.Value = $"{baseKey}|{seq}";
        }
        p.Submit();
    }

    PreviewLines.Reload();
    CaptureSnapshot();
}

// ============ 登録（プレビュー → 本番へ移送。ルール適用を含む） ============

void ConfirmImport_OnClick()
{
    // プレビューを本テーブルへ確定する一括操作。確定後はプレビューの破棄では戻せない（ADR-0062）
    var answer = MessageBox.Show(
        "プレビュー中の明細をまとめて銀行明細に登録します。登録後は「取込をやめる」では戻せません。よろしいですか？",
        "登録する", "キャンセル");
    if (answer != "登録する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 画面上の編集・削除を反映してから登録する（B-2 の恒久対策）
    SavePreviewEdits();

    var s = new ModuleSearcher<BankStatementPreview>();
    s.OrderBy(e => e.LineDate.Value);
    var rows = s.Execute();
    if (rows.Count == 0) { Toaster.Info("プレビュー中の明細がありません"); return; }

    // 有効なマッチングルール（優先度順）— 登録時に候補が空の行へ自動適用（Q-a 案1）
    var rs = new ModuleSearcher<MatchingRule>();
    rs.AddEquals(r => r.IsActive.Value, true);
    rs.OrderBy(r => r.Priority.Value);
    var rules = rs.Execute();

    // 本番側の既存キー（二重登録の最終ガード）
    var existKeys = new List<string>();
    var es = new ModuleSearcher<BankStatementLine>();
    foreach (var em in es.Execute())
    {
        var ex = (BankStatementLine)em;
        if (ex.DedupKey.Value != null) existKeys.Add($"{ex.BankAccount.Value}|{ex.DedupKey.Value}");
    }

    var done = 0;
    var ruled = 0;
    var dup = 0;
    foreach (var m in rows)
    {
        var p = (BankStatementPreview)m;
        var guardKey = $"{p.BankAccount.Value}|{p.DedupKey.Value}";
        if (existKeys.Contains(guardKey))
        {
            dup = dup + 1;
            p.Delete();
            continue;
        }

        var line = new BankStatementLine();
        line.BankAccount.Value = p.BankAccount.Value;
        line.LineDate.Value = p.LineDate.Value;
        line.Description.Value = p.Description.Value;
        line.AmountOut.Value = p.AmountOut.Value;
        line.AmountIn.Value = p.AmountIn.Value;
        line.Balance.Value = p.Balance.Value;
        line.DedupKey.Value = p.DedupKey.Value;
        line.Status.Value = "pending";
        line.ImportedAt.Value = p.ImportedAt.Value;

        // ルール適用（摘要キーワードの部分一致・優先度順で最初の一致のみ）
        var desc = p.Description.Value ?? "";
        var outAmt = p.AmountOut.Value ?? 0;
        var inAmt = p.AmountIn.Value ?? 0;
        foreach (var rm in rules)
        {
            var rule = (MatchingRule)rm;
            var kw = rule.Keyword.Value;
            if (kw == null || kw == "") continue;
            var dir = rule.Direction.Value;
            if (dir == "in" && inAmt <= 0) continue;
            if (dir == "out" && outAmt <= 0) continue;
            if (!desc.Contains(kw)) continue;
            line.SuggestedAccount.Value = rule.Account.Value;
            line.SuggestionSource.Value = "rule";
            ruled = ruled + 1;
            break;
        }

        var ok = line.Submit();
        if (ok == true)
        {
            existKeys.Add(guardKey);
            p.Delete();
            done = done + 1;
        }
    }

    PreviewLines.Reload();
    CaptureSnapshot();
    ResultLabel.Text = $"{done} 件を未起票の明細として登録しました（ルール適用 {ruled} 件 / 重複スキップ {dup} 件）。科目の確定と起票は「一括起票」で行います";
    Toaster.Success($"{done} 件の明細を登録しました");
}

// ============ 全プレビューの取り消し ============

void CancelImport_OnClick()
{
    var s = new ModuleSearcher<BankStatementPreview>();
    var rows = s.Execute();
    if (rows.Count == 0) { Toaster.Info("プレビュー中の明細がありません"); return; }

    var answer = MessageBox.Show($"プレビュー {rows.Count} 件をすべて取り消します（重複キーが解放され、同じ CSV を貼り直せます）。よろしいですか？", "取り消す", "キャンセル");
    if (answer != "取り消す") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var done = 0;
    foreach (var m in rows)
    {
        var r = (BankStatementPreview)m;
        var ok = r.Delete();
        if (ok == true) { done = done + 1; }
    }
    PreviewLines.Reload();
    CaptureSnapshot();
    ResultLabel.Text = $"プレビュー {done} 件を取り消しました";
    Toaster.Info($"プレビュー {done} 件を取り消しました（同じ CSV を貼り直せます）");
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
