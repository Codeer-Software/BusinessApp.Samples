// JournalImport.mod.cs — 仕訳CSVインポート（D-5・汎用）
// 他ソフトからの移行・給与計算ソフトの仕訳データ取込の受け皿。
// 形式: 伝票グループ,日付,借貸,科目コード,金額,税区分コード,摘要（1行=1明細行）
// 方針: 行は無加工で取り込む（税の再計算・税行の自動生成はしない。移行の正確性優先）。
//       ただし税区分だけは空欄を補完する（ADR-0052。未設定の行は作らない）。
// 検証はグループ（伝票）単位: 1行でも NG のグループは伝票ごとスキップして理由を集計する。
// 生成は JournalEntry 単位の je.Submit()（表示専用モジュールの this.Submit() は機能しない）。
// 注: グループエラーの記録は List への追記＋線形探索で行う（インデクサ書き込み・
//     Dictionary・ジェネリック引数のヘルパはスクリプトで未実証のため使わない）。

void Detail_OnAfterInit()
{
    ResultLabel.Text = "";
}

void Import_OnClick()
{
    var raw = CsvText.Value;
    if (raw == null || raw.Trim() == "") { Toaster.Error("仕訳CSVを貼り付けてください"); return; }

    // 一括で確定仕訳を作る操作なので確認する（ADR-0062）
    var answer = MessageBox.Show(
        "貼り付けた CSV から仕訳を一括で取り込みます。取り込んだ仕訳は確定として帳簿に載り、"
        + "まとめて取り消す導線はありません（誤りは伝票ごとに訂正が必要です）。よろしいですか？",
        "取り込む", "キャンセル");
    if (answer != "取り込む") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // マスタ一括ロード（ループ内 I/O の削減）
    var accS = new ModuleSearcher<Account>();
    var accounts = accS.Execute();
    var tcS = new ModuleSearcher<TaxCategory>();
    var taxCats = tcS.Execute();
    var fyS = new ModuleSearcher<FiscalYear>();
    var years = fyS.Execute();
    var fpS = new ModuleSearcher<FiscalPeriod>();
    var periods = fpS.Execute();

    // ---- 解析（グループと行を並行リストに集める） ----
    var gKeys = new List<string>();     // グループキー（出現順）
    var gDate = new List<string>();     // グループの日付（先頭行の正規化日付）
    var errGi = new List<int>();        // グループエラー（追記のみ・最初の理由が有効）
    var errMsg = new List<string>();

    var rGroup = new List<int>();       // 行→グループ index
    var rDc = new List<string>();
    var rAccountId = new List<object>();
    var rAmount = new List<int>();
    var rTaxCatId = new List<object>();
    var rIsTaxLine = new List<bool>();
    var rDesc = new List<string>();
    var rParentRi = new List<int>();    // 税行 → 親（本体行）の行 index。-1 は未解決（BUG-0063）

    var badLines = 0;

    var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");
    var lines = text.Split('\n');
    foreach (var line in lines)
    {
        var t = line.Trim();
        if (t == "") continue;
        var cols = SplitCsvLine(t);
        if (cols.Count < 5) { badLines = badLines + 1; continue; }

        var groupKey = cols[0].Trim();
        var dateStr = NormalizeDate(cols[1]);
        var amount = ParseAmount(cols[4]);

        // ヘッダ行・ゴミ行: 日付も金額も解析できない行は黙ってスキップ
        if (dateStr == "" && amount == 0) { badLines = badLines + 1; continue; }
        if (groupKey == "") { badLines = badLines + 1; continue; }

        // グループの解決（出現順に採る）
        var gi = -1;
        for (int i = 0; i < gKeys.Count; i++)
        {
            if (gKeys[i] == groupKey) { gi = i; break; }
        }
        if (gi < 0)
        {
            gKeys.Add(groupKey);
            gDate.Add(dateStr);
            gi = gKeys.Count - 1;
        }

        // 行レベル検証（NG はグループごとスキップ。最初の理由のみ記録）
        var rowErr = "";
        if (dateStr == "")
        {
            rowErr = "日付が解析できない行があります";
        }
        else if (gDate[gi] != dateStr)
        {
            rowErr = "同一伝票内で日付が一致しません";
        }

        var dc = NormalizeDc(cols[2]);
        if (rowErr == "" && dc == "")
        {
            rowErr = $"借貸が不正です（{cols[2]}）";
        }

        var code = cols[3].Trim();
        object accountId = null;
        foreach (var am in accounts)
        {
            var a = (Account)am;
            if (a.Code.Value == code) { accountId = a.Id.Value; break; }
        }
        // 消費税勘定（仮払1900/仮受2200）は税行として取り込む（未設定だと消費税集計表の課税標準に混入する）
        var isTaxLine = (code == "1900" || code == "2200");
        if (rowErr == "" && accountId == null)
        {
            rowErr = $"科目コード {code} が存在しません";
        }

        object taxCatId = null;
        var taxCode = (cols.Count > 5) ? cols[5].Trim() : "";
        if (taxCode != "")
        {
            foreach (var cm in taxCats)
            {
                var c = (TaxCategory)cm;
                if (c.Code.Value == taxCode) { taxCatId = c.Id.Value; break; }
            }
            if (rowErr == "" && taxCatId == null)
            {
                rowErr = $"税区分コード {taxCode} が存在しません";
            }
        }

        if (rowErr == "" && amount <= 0)
        {
            rowErr = "金額は 1 円以上で入力してください";
        }

        if (rowErr != "")
        {
            var hasErr = false;
            for (int i = 0; i < errGi.Count; i++)
            {
                if (errGi[i] == gi) { hasErr = true; break; }
            }
            if (!hasErr) { errGi.Add(gi); errMsg.Add(rowErr); }
        }

        rGroup.Add(gi);
        rDc.Add(dc);
        rAccountId.Add(accountId);
        rAmount.Add(amount);
        rTaxCatId.Add(taxCatId);
        rIsTaxLine.Add(isTaxLine);
        rParentRi.Add(-1);
        rDesc.Add((cols.Count > 6) ? cols[6].Trim() : "");
    }

    if (gKeys.Count == 0)
    {
        ResultLabel.Text = $"取込 0 伝票（解析不能行 {badLines} 行）";
        Toaster.Warn("取り込める仕訳がありませんでした");
        return;
    }

    // ---- 税区分の補完（ADR-0052: 税区分未設定の行を作らない） ----
    // 空欄は勘定科目マスタの既定で埋める。ただし税行（仮払/仮受消費税）は本体行に対する消費税なので
    // 本体行と同じ税区分が正しく、科目の既定（＝対象外）を入れると集計表からその税額が消える（B-5 の再発）。
    // 税行は同一伝票の本体行から継承し、推定できないものは伝票ごとスキップして税区分コードの指定を促す。
    // 親行の決定は**税区分が明示されている税行にも要る**（BUG-0063 の当て漏れ）。
    // 税区分の補完ループは「税区分が空の行」しか通らないので、親行の記録だけ先に全行ぶん済ませる
    for (int ri = 0; ri < rGroup.Count; ri++)
    {
        if (!rIsTaxLine[ri]) continue;
        if (rParentRi[ri] >= 0) continue;
        for (int rj = 0; rj < rGroup.Count; rj++)
        {
            if (rGroup[rj] != rGroup[ri]) continue;
            if (rIsTaxLine[rj]) continue;
            if (rDc[rj] != rDc[ri]) continue;         // 貸借が同じ本体行を親にする
            rParentRi[ri] = rj;
            break;
        }
    }

    for (int ri = 0; ri < rGroup.Count; ri++)
    {
        if (rTaxCatId[ri] != null) continue;
        if (rAccountId[ri] == null) continue;   // 科目コード不正は解析時に記録済み

        object resolved = null;
        var failReason = "";

        if (rIsTaxLine[ri])
        {
            object found = null;
            var conflict = false;
            for (int rj = 0; rj < rGroup.Count; rj++)
            {
                if (rGroup[rj] != rGroup[ri]) continue;
                if (rIsTaxLine[rj]) continue;
                if (rTaxCatId[rj] == null) continue;
                if (found == null) { found = rTaxCatId[rj]; rParentRi[ri] = rj; }
                else if ($"{found}" != $"{rTaxCatId[rj]}") { conflict = true; break; }
            }
            if (conflict)
            {
                failReason = "税行の税区分を推定できません（本体行の税区分が複数あります）。税区分コードを指定してください";
            }
            else if (found == null)
            {
                failReason = "税行の税区分を推定できません（本体行に税区分がありません）。税区分コードを指定してください";
            }
            else
            {
                resolved = found;
            }
        }
        else
        {
            foreach (var am in accounts)
            {
                var a = (Account)am;
                if ($"{a.Id.Value}" == $"{rAccountId[ri]}") { resolved = a.DefaultTaxCategory.Value; break; }
            }
            if (resolved == null)
            {
                failReason = "科目に既定税区分がありません。税区分コードを指定してください";
            }
        }

        if (resolved != null)
        {
            rTaxCatId[ri] = resolved;
            continue;
        }

        var errGroup = rGroup[ri];
        var errSeen = false;
        for (int i = 0; i < errGi.Count; i++)
        {
            if (errGi[i] == errGroup) { errSeen = true; break; }
        }
        if (!errSeen) { errGi.Add(errGroup); errMsg.Add(failReason); }
    }

    // ---- グループ単位の検証（貸借一致・期間 open） ----
    for (int gi = 0; gi < gKeys.Count; gi++)
    {
        var hasErr = false;
        for (int i = 0; i < errGi.Count; i++)
        {
            if (errGi[i] == gi) { hasErr = true; break; }
        }
        if (hasErr) continue;

        var groupErr = "";
        var dSum = 0;
        var cSum = 0;
        var rowCount = 0;
        for (int ri = 0; ri < rGroup.Count; ri++)
        {
            if (rGroup[ri] != gi) continue;
            rowCount = rowCount + 1;
            if (rDc[ri] == "D") { dSum = dSum + rAmount[ri]; }
            else { cSum = cSum + rAmount[ri]; }
        }
        if (rowCount == 0)
        {
            groupErr = "明細行がありません";
        }
        else if (dSum != cSum)
        {
            groupErr = $"貸借不一致（借方 {dSum:#,0} / 貸方 {cSum:#,0}）";
        }

        if (groupErr == "")
        {
            // 年度・期間の解決は境界日の罠を避けるため月初日で行う
            var gd = ToDate(gDate[gi]);
            var firstDay = DateOnly.FromDateTime(new DateTime(gd.Year, gd.Month, 1));
            var yearFound = false;
            foreach (var ym in years)
            {
                var y = (FiscalYear)ym;
                if (y.StartDate.Value <= firstDay && y.EndDate.Value >= firstDay) { yearFound = true; break; }
            }
            if (!yearFound)
            {
                groupErr = $"{gDate[gi]} に対応する会計年度がありません";
            }
            else
            {
                var periodFound = false;
                var periodOpen = false;
                foreach (var pm in periods)
                {
                    var p = (FiscalPeriod)pm;
                    if (p.StartDate.Value <= firstDay && p.EndDate.Value >= firstDay)
                    {
                        periodFound = true;
                        if (p.Status.Value != "closed") { periodOpen = true; }
                        break;
                    }
                }
                if (!periodFound)
                {
                    groupErr = $"{gDate[gi]} に対応する月次期間がありません";
                }
                else if (!periodOpen)
                {
                    groupErr = $"{gDate[gi]} の期間は締め済みです";
                }
            }
        }

        if (groupErr != "")
        {
            errGi.Add(gi);
            errMsg.Add(groupErr);
        }
    }

    // ---- 伝票の生成（出現順。伝票番号は年度内連番で再採番） ----
    var asDraft = (AsDraftCheck.Value == true);
    var importedEntries = 0;
    var importedLines = 0;
    var skippedGroups = 0;
    var failedGroups = 0;

    for (int gi = 0; gi < gKeys.Count; gi++)
    {
        var hasErr = false;
        for (int i = 0; i < errGi.Count; i++)
        {
            if (errGi[i] == gi) { hasErr = true; break; }
        }
        if (hasErr) { skippedGroups = skippedGroups + 1; continue; }

        var idxs = new List<int>();
        for (int ri = 0; ri < rGroup.Count; ri++)
        {
            if (rGroup[ri] == gi) idxs.Add(ri);
        }

        var gd = ToDate(gDate[gi]);
        var firstDay = DateOnly.FromDateTime(new DateTime(gd.Year, gd.Month, 1));
        object fyId = null;
        foreach (var ym in years)
        {
            var y = (FiscalYear)ym;
            if (y.StartDate.Value <= firstDay && y.EndDate.Value >= firstDay) { fyId = y.Id.Value; break; }
        }
        if (fyId == null) { skippedGroups = skippedGroups + 1; continue; }

        var je = new JournalEntry();
        je.EntryDate.Value = DateOnly.FromDateTime(gd);
        je.EntryType.Value = "transfer";
        je.FiscalYearRef.Value = fyId;
        je.SourceType.Value = "import";
        var headDesc = rDesc[idxs[0]];
        if (headDesc == "") { headDesc = $"インポート {gKeys[gi]}"; }
        je.Description.Value = headDesc;
        if (asDraft)
        {
            je.Status.Value = "draft";
        }
        else
        {
            je.Status.Value = "posted";
            // 年度内連番の採番（伝票ごとに直前の最大値を取得。BankImport と同方式）。
            // 正典: JournalEntry.NextJournalNo（BUG-0069 で一本化）
            je.JournalNo.Value = je.NextJournalNo(fyId);
        }

        je.Lines.AddRows(idxs.Count);
        var li = 0;
        foreach (var lr in je.Lines.Rows)
        {
            var l = (JournalLine)lr;
            var ri = idxs[li];
            li = li + 1;
            l.LineNo.Value = li;
            l.Dc.Value = rDc[ri];
            l.Account.Value = rAccountId[ri];
            l.Amount.Value = rAmount[ri];
            l.InputAmount.Value = rAmount[ri];
            if (rTaxCatId[ri] != null) { l.TaxCategory.Value = rTaxCatId[ri]; }
            if (rIsTaxLine[ri])
            {
                l.IsTaxLine.Value = true;
                // 税行の親行を記録する（BUG-0063）。これが無いと税額を本体行へ遡って辿れず、
                // 不変条件 A09（消費税行と親行の整合）が成立しない。
                // 親は「税区分の継承元にした本体行」＝上の補完ループで決めた行と同じものを使う
                // （別の決め方をすると、税区分は A 行から・親は B 行、という食い違いが起きる）
                if (rParentRi[ri] >= 0)
                {
                    var pln = 0;
                    for (int k = 0; k < idxs.Count; k++)
                    {
                        if (idxs[k] == rParentRi[ri]) { pln = k + 1; break; }
                    }
                    if (pln > 0) { l.ParentLineNo.Value = pln; }
                }
            }
            l.TaxInputMode.Value = "none";
            l.Description.Value = rDesc[ri];
        }

        je.MarkRemainingLinesOutOfScope();   // 上の補完で全行埋まっているはずだが、経路共通の保険として通す
        je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
        var ok = je.Submit();
        if (ok != true)
        {
            failedGroups = failedGroups + 1;
            continue;
        }
        importedEntries = importedEntries + 1;
        importedLines = importedLines + idxs.Count;
    }

    // ---- 結果表示（スキップ理由は先頭3件まで） ----
    var reasons = "";
    var shown = 0;
    for (int i = 0; i < errGi.Count; i++)
    {
        if (shown >= 3) { reasons = reasons + " ほか"; break; }
        reasons = reasons + $" [{gKeys[errGi[i]]}: {errMsg[i]}]";
        shown = shown + 1;
    }

    var statusNote = "";
    if (asDraft) { statusNote = "（下書き）"; }
    ResultLabel.Text = $"取込 {importedEntries} 伝票（{importedLines} 行）{statusNote} / スキップ {skippedGroups} 伝票{reasons} / 保存失敗 {failedGroups} / 解析不能行 {badLines} 行";
    if (importedEntries > 0)
    {
        // **貼り付けたテキストを消す**（BUG-0070）。取込は冪等でなく、まとめて取り消す導線も無いので、
        // 「成功したか確信が持てずもう一度押す」だけで**全伝票が二重計上**される。
        // 結果ラベルは残すので何件入ったかは読める。もう一度取り込みたいなら貼り直す＝意思表示になる。
        CsvText.Value = "";
        Toaster.Success($"{importedEntries} 伝票（{importedLines} 行）を取り込みました{statusNote}。"
            + "取り消す導線はありません（誤りは伝票ごとに訂正してください）。貼り付け欄は二重取込を防ぐため空にしました");
    }
    else
    {
        Toaster.Warn("取り込めた伝票がありません。スキップ理由を確認してください");
    }
}

// ============ ヘルパ ============

// 借貸の正規化: D/C・借/貸・借方/貸方 を受理。不明は ""
string NormalizeDc(string s)
{
    var t = s.Trim().Replace("\"", "").ToUpper();
    if (t == "D" || t == "借" || t == "借方") return "D";
    if (t == "C" || t == "貸" || t == "貸方") return "C";
    return "";
}

// CSV 1行の分割（引用符内カンマ対応。BankImport と同実装）
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

// 日付文字列の正規化（BankImport と同実装）。日付でなければ "" を返す
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
    if (dt.Month != m) return "";
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

// 金額文字列 → 円整数（BankImport と同実装）
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
