// 申請一覧の既定検索: 申請者=自分（2026-08-06 ユーザー要望）。
// 経理は全社の申請を見る運用のため既定を付けない。検索ページの Link は SearchValue 系統（#48）
void Search_OnInit()
{
    if (CurrentUser.HasAccountingAccess.Value == true) return;
    Creator.SearchValue = CurrentUser.Id.Value;
}

// ============================================================
// 明細（ADR-0066）
// 画面は 4 ブロック構成:
//   1. この申請について（件名・目的・計上日・支払先など）
//   2. **明細 1 件分の入力フォーム** ＝ EditingLine（ModuleField で埋め込んだ明細行そのもの）
//   3. 追加した明細のリスト（line_no を採番済みの行）
//   4. 承認
//
// 「2」が明細行の実体であることが要点。AI 読み取りと領収書の添付を最初からその行が持つので、
// 「フォームで読ませた領収書をもう一度リストに添付し直す」という二度手間が起きない。
//
// 入力中の行は expense_request_id・line_no のどちらも空。リスト（親 FK で絞る）にも
// 合計・仕訳・検証（GetLines）にも出てこない。「追加」で初めて両方が入り、明細になる。
// ============================================================

// 確定済みの明細（この申請にひもづいた行）。入力中の行はまだ親を持たないので出てこない
List<ExpenseRequestLine> GetLines()
{
    return GetLinesFromDb();
}

// 明細を DB から取得（保存済みの内容が正）
List<ExpenseRequestLine> GetLinesFromDb()
{
    var result = new List<ExpenseRequestLine>();
    if (this.IsNewData) return result;
    var s = new ModuleSearcher<ExpenseRequestLine>();
    s.AddEquals(l => l.ExpenseRequestId.Value, this.Id.Value);
    s.OrderBy(l => l.LineNo.Value);
    foreach (var m in s.Execute())
    {
        var l = (ExpenseRequestLine)m;
        if (l.LineNo.Value == null) continue;   // 保険: 行番号が無い行は明細として扱わない
        result.Add(l);
    }
    return result;
}

// ============================================================
// ブロック 2（明細 1 件分の入力フォーム）の操作
//
// 入力中の行は expense_request_id が空のまま作られ、親からは editing_line_id だけで指される。
// そのため**確定するまで明細リストにも合計にも出てこない**。
// 「追加」で初めて親と行番号がひもづき、リストに現れる。
// ============================================================

// 「この内容で明細に追加」: 入力中の行を確定してリストへ送り、入力欄を空に戻す
void AddLine_OnClick()
{
    CommitEntry();
}

// 「この明細を更新」: リストから読み込んだ行を上書き保存する
void UpdateLine_OnClick()
{
    CommitEntry();
}

// 「入力内容をクリア」/「編集をやめる」: 入力中の内容を捨てて空の入力欄に戻す（確定済みの行には触らない）
void CancelEdit_OnClick()
{
    var line = EditingLine.ChildModule;
    if (line == null) return;

    // 新規入力の書きかけ: 行はまだどこにも属していないので、その場で項目を空に戻すだけでよい。
    // （申請そのものが未保存のときは Reload/Submit ができない——Id が @temporary: のため）
    if (line.LineNo.Value == null)
    {
        ClearEntryFields(line);
        Toaster.Info("入力欄を空に戻しました");
        return;
    }

    // 既存の明細を読み込んで編集していた場合: 変更を捨てて、入力欄を新しい空の行に戻す。
    // 確定済みの明細そのものには触らない（リストにはそのまま残る）
    using var loading = LoadingService.StartLoading(0);
    this.Reload();                       // 入力欄の変更を先に捨てる（次の Submit で保存させない）
    EditingLineIdRaw.Value = null;
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("入力欄を戻せませんでした"); return; }
    this.Reload();
    Toaster.Info("編集をやめました（明細はそのままです）");
}

// 入力欄の項目をすべて空に戻す（行そのものは残す＝ModuleField が抱えている実体なので消せない）
void ClearEntryFields(ExpenseRequestLine line)
{
    using var suspend = this.SuspendNotifyStateChanged();
    line.UsedDate.Value = null;
    line.ExpenseCategoryRef.Value = null;
    line.TaxCategoryRef.Value = null;
    line.Amount.Value = null;
    line.TaxAmount.Value = null;
    line.ProjectRef.Value = null;
    line.UsedAt.Value = null;
    line.Description.Value = null;
    line.IsFixedAsset.Value = false;
    line.AssetNo.Value = null;
    line.EntertainmentGuest.Value = null;
    line.EntertainmentCount.Value = null;
    line.EntertainmentPurpose.Value = null;
    line.Receipt.ClearFile();
}

// 入力中の行を確定する。新規なら行番号を採番し、編集中なら既存の行番号を保つ
void CommitEntry()
{
    var line = EditingLine.ChildModule;
    if (line == null) { Toaster.Error("入力欄が用意されていません"); return; }
    if (!ValidateEntry(line)) return;

    var isUpdate = (line.LineNo.Value != null);

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    if (!isUpdate) { line.LineNo.Value = NextLineNo(); }
    line.ExpenseRequestId.Value = this.Id.Value;

    // 1 回目: 入力中の行を親にひもづけて保存する（ここで初めて明細になる）
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("明細の保存に失敗しました"); return; }

    // 2 回目: 入力欄を空に戻し（FK を外す）、確定した明細から合計を取り直す
    EditingLineIdRaw.Value = null;
    RecalcFromLines();
    var ret2 = this.Submit();
    if (ret2 == false) { Toaster.Error("合計の保存に失敗しました"); return; }

    // 読み直して、空の入力欄と最新の明細リストにする
    this.Reload();
    if (isUpdate) { Toaster.Success($"{line.LineNo.Value} 件目の明細を更新しました"); }
    else { Toaster.Success($"明細に追加しました（{line.LineNo.Value} 件目）"); }
}

// 入力中の行の検証（メッセージは行番号を出さない——見えているのはこの 1 件だけなので）
bool ValidateEntry(ExpenseRequestLine line)
{
    if (line.Amount.Value == null || line.Amount.Value <= 0)
    {
        Toaster.Error("金額（税込）を入力してください");
        return false;
    }
    if (line.UsedDate.Value == null)
    {
        Toaster.Error("利用日を入力してください");
        return false;
    }
    var cat = FindCategory(line.ExpenseCategoryRef.Value);
    if (cat == null)
    {
        Toaster.Error("費目を選択してください");
        return false;
    }
    if (line.TaxCategoryRef.Value == null)
    {
        line.TaxCategoryRef.Value = cat.DefaultTaxCategory.Value;
    }
    if (cat.IsEntertainment.Value == true)
    {
        var guestOk = !string.IsNullOrEmpty(line.EntertainmentGuest.Value);
        var countOk = (line.EntertainmentCount.Value ?? 0) > 0;
        var purposeOk = !string.IsNullOrEmpty(line.EntertainmentPurpose.Value);
        if (!guestOk || !countOk || !purposeOk)
        {
            Toaster.Error("交際費は相手先・参加人数・目的の入力が必須です（「交際費の記録」を開いてください）");
            return false;
        }
    }
    if (!ValidateLineTax(line, cat, 0)) return false;
    if (line.Receipt.FileName == null || line.Receipt.FileName == "")
    {
        Toaster.Warn("領収書が添付されていません。紙の原本を保管してください（申請後は添付できません）");
    }
    return true;
}

// 次の行番号（確定済みの最大 + 1）
int NextLineNo()
{
    var max = 0;
    foreach (var l in GetLinesFromDb())
    {
        var n = l.LineNo.Value ?? 0;
        if (n > max) max = n;
    }
    return max + 1;
}

// リストの行を選ぶとその明細が入力欄に載る（編集の入口はここ 1 つだけ）。
//
// 実測（2026-08-17）: 画面に出ている ModuleField は、保存のたびに自分が抱えている子の Id を
// DB 列（editing_line_id）へ書き戻す。同じ列を指す EditingLineIdRaw に別の行の Id を入れても
// 上書きされてしまうので、**画面の外**（DB から取り直した別インスタンス）で張り替えてから
// ページを開き直す。開き直した時点の列の値で ModuleField が子を読むので、狙った行が載る。
void Lines_OnSelectedIndexChanged()
{
    if (!CanEditLines()) return;
    var idx = Lines.SelectedIndex;
    if (idx < 0) return;
    var l = (ExpenseRequestLine)Lines.Rows[idx];
    if (l == null || l.Id.Value == null) return;
    if (IsSameId(EditingLineIdRaw.Value, l.Id.Value)) return;   // 既に載っている行なら何もしない
    if (HasPendingEntry())
    {
        Toaster.Error("入力欄に書きかけの内容があります。先に「この内容で明細に追加」か「入力内容をクリア」を押してください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);

    // 件名などヘッダの編集を落とさないよう、先に保存する
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("保存に失敗したため明細を開けませんでした"); return; }
    if (!PointEditingLineTo(l.Id.Value)) { Toaster.Error("明細を開けませんでした"); return; }

    // DB 側の editing_line_id を張り替えたので、読み直せば ModuleField がその行を載せる
    this.Reload();
    UpdateLineButtons();
    Toaster.Info($"{l.LineNo.Value} 件目を上の入力欄に読み込みました。直して「この明細を更新」を押してください");
}

// 親の editing_line_id を指定の明細へ張り替える（画面の ModuleField を経由しない）
bool PointEditingLineTo(object lineId)
{
    var s = new ModuleSearcher<ExpenseRequest>();
    s.AddEquals(e => e.Id.Value, this.Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return false;
    var other = (ExpenseRequest)found;
    var oldId = other.EditingLineIdRaw.Value;
    if (IsSameId(oldId, lineId)) return true;
    other.EditingLineIdRaw.Value = lineId;
    if (other.Submit() == false) return false;
    DeleteOrphanEntryLine(oldId);
    return true;
}

// 誰からも参照されなくなった空の入力欄の行を片付ける（親も行番号も持たない行だけ消す）
void DeleteOrphanEntryLine(object lineId)
{
    if (lineId == null) return;
    var s = new ModuleSearcher<ExpenseRequestLine>();
    s.AddEquals(l => l.Id.Value, lineId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return;
    var line = (ExpenseRequestLine)found;
    if (line.ExpenseRequestId.Value != null) return;
    if (line.LineNo.Value != null) return;
    var ret = line.Delete();
    // 消せなくても業務影響は無い（どこからも参照されない空行が 1 件残るだけ）。黙って握らず記録だけする
    if (ret != true) { Logger.Warn($"使い終わった入力欄の行（id={lineId}）を片付けられませんでした"); }
}

// 明細を編集できる状態か（下書きのあいだと、事前申請の実費確定待ちのあいだ）
bool CanEditLines()
{
    if (IsNewData) return true;
    var st = SettlementStatus.Value;
    if (st == null || st == "" || st == "draft") return true;
    var isAdvance = (RequestType.Value == "advance");
    return (st == "approved") && isAdvance && (ActualConfirmed.Value != true);
}

// 入力欄が「書きかけ」か（追加も更新もされていない内容が残っているか）
bool HasPendingEntry()
{
    var line = EditingLine.ChildModule;
    if (line == null) return false;
    if (line.LineNo.Value != null) return true;                 // 既存行を読み込んで編集中
    if ((line.Amount.Value ?? 0) > 0) return true;
    if (line.ExpenseCategoryRef.Value != null) return true;
    if (line.UsedDate.Value != null) return true;
    if (!string.IsNullOrEmpty(line.UsedAt.Value)) return true;
    if (!string.IsNullOrEmpty(line.Description.Value)) return true;
    if (!string.IsNullOrEmpty(line.Receipt.FileName)) return true;
    return false;
}

// ブロック 2・3 の出し分け（新規入力なのか、既存行の編集なのかでボタンを変える）
void UpdateLineButtons()
{
    var canEdit = CanEditLines();
    var line = EditingLine.ChildModule;
    var isExisting = (line != null) && (line.LineNo.Value != null);

    EntryLabel.IsVisible = canEdit;
    EntryHint.IsVisible = canEdit;
    EditingLine.IsVisible = canEdit;
    AddLineButton.IsVisible = canEdit && !isExisting;
    UpdateLineButton.IsVisible = canEdit && isExisting;
    CancelEditButton.IsVisible = canEdit;

    if (isExisting)
    {
        EntryLabel.Text = "2. 明細を編集する";
        EntryHint.Text = "下のリストから読み込んだ明細です。直して「この明細を更新」を押してください";
        CancelEditButton.Text = "編集をやめる";
    }
    else
    {
        EntryLabel.Text = "2. 明細を入力する";
        EntryHint.Text = "レシート 1 枚ぶんを入力して「この内容で明細に追加」を押すと、下のリストに積まれます。領収書を選ぶと AI 読み取りが使えます";
        CancelEditButton.Text = "入力内容をクリア";
    }
    LinesHint.IsVisible = canEdit;
}

// 明細リストの変更（行の削除）→ 削除を確定してから採番を詰め、合計を取り直す。
// 税区分の補完・少額資産の判定は入力フォーム側（ExpenseRequestLine）が受け持つので、ここは持たない。
void Lines_OnDataChanged()
{
    if (IsNewData) return;
    if (!CanEditLines()) return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 先に削除を DB へ反映する（反映前に集計すると消したはずの行が合計に残る）
    var ret = this.Submit();
    if (ret == false) { Toaster.Error("明細の削除に失敗しました"); return; }

    RenumberLines();
    RecalcFromLines();
    this.Submit();
    UpdateLineButtons();
}

// 明細の行番号を 1 から振り直す（削除で空いた番号を詰める）
void RenumberLines()
{
    var n = 0;
    foreach (var l in GetLinesFromDb())
    {
        n = n + 1;
        if (l.LineNo.Value == n) continue;
        l.LineNo.Value = n;
        l.Submit();
    }
}

// 合計金額・うち消費税は確定済みの明細から導出する（手入力しない・ADR-0066）。
// 経費は「レシート記載の税額が正」なので、税額は**行ごとに**確定させて単純合計する
// （税率ごとに 1 回だけ端数処理する ADR-0050 は自社が発行する請求書側の規約であり、ここには当てはまらない）。
void RecalcFromLines()
{
    var total = 0;
    var tax = 0;
    var lastUsed = ExpenseDate.Value;
    var hasUsed = false;
    foreach (var l in GetLinesFromDb())
    {
        if (l.Amount.Value != null) total = total + l.Amount.Value;
        tax = tax + CalcLineTax(l);
        if (l.UsedDate.Value != null)
        {
            if (!hasUsed || l.UsedDate.Value > lastUsed)
            {
                lastUsed = l.UsedDate.Value;
                hasUsed = true;
            }
        }
    }
    Amount.Value = total;
    TaxAmount.Value = tax;

    // 計上日の既定は「明細でいちばん遅い利用日」。下書きのうちだけ追随させる
    // （申請後に日付が動くと承認済みの内容が変わってしまうため）
    if (hasUsed && (SettlementStatus.Value == null || SettlementStatus.Value == "draft"))
    {
        ExpenseDate.Value = lastUsed;
    }
}

// 行の税額: 行の税区分が課税仕入のときだけ。レシート記載（手入力）を優先し、無ければ内税計算（切り捨て）
int CalcLineTax(ExpenseRequestLine l)
{
    if (l == null) return 0;
    if (l.Amount.Value == null || l.Amount.Value <= 0) return 0;
    var tcat = ResolveLineTaxCategory(l, FindCategory(l.ExpenseCategoryRef.Value));
    if (!IsTaxablePurchaseTaxCategory(tcat)) return 0;
    if (l.TaxAmount.Value != null && l.TaxAmount.Value > 0) return l.TaxAmount.Value;
    decimal pct = GetTaxRatePercent(tcat);
    if (pct == 0) return 0;
    int gross = l.Amount.Value;
    int tax = gross * pct / (100 + pct);
    return tax;
}

// 行の税区分: 行に設定があればそれ、無ければ費目の既定
TaxCategory ResolveLineTaxCategory(ExpenseRequestLine l, ExpenseCategory cat)
{
    object id = null;
    if (l != null && l.TaxCategoryRef.Value != null) id = l.TaxCategoryRef.Value;
    else if (cat != null) id = cat.DefaultTaxCategory.Value;
    if (id == null) return null;
    var s = new ModuleSearcher<TaxCategory>();
    s.AddEquals(c => c.Id.Value, id);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (TaxCategory)found;
}

bool IsTaxablePurchaseTaxCategory(TaxCategory tcat)
{
    if (tcat == null) return false;
    return (tcat.TaxationType.Value == "taxable_purchase");
}

// 費目マスタの取得（未選択・解決不能なら null）
ExpenseCategory FindCategory(object categoryId)
{
    if (categoryId == null) return null;
    var s = new ModuleSearcher<ExpenseCategory>();
    s.AddEquals(c => c.Id.Value, categoryId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (ExpenseCategory)found;
}

// ============================================================
// 承認ルートの解決（ADR-0066）
// 判定額は申請合計（分割して上位承認を回避できないようにするため）。費目は行ごと。
// 行ごとに approval_route_rules を first-match で解決し、テンプレートを重複なく集める。
// 段の重ね合わせ（合成）は ApprovalFlow.BuildMergedOrders が行う。
// 明細が 1 行なら結果は従来とまったく同じ 1 テンプレートになる。
// ============================================================
List<object> SelectTemplateIds()
{
    var result = new List<object>();
    var amount = GetJudgeAmount();

    // 行の費目（重複除去）。まだ明細が無い段階（新規初期化時）は空を返し、呼び出し側が仮値として扱う
    var catIds = new List<object>();
    foreach (var l in GetLines())
    {
        var cid = l.ExpenseCategoryRef.Value;
        if (cid == null) continue;
        if (!ContainsId(catIds, cid)) catIds.Add(cid);
    }
    if (catIds.Count == 0) return result;

    var rs = new ModuleSearcher<ApprovalRouteRule>();
    rs.OrderBy(r => r.Priority.Value);
    var rules = rs.Execute();

    foreach (var catId in catIds)
    {
        object matched = null;
        foreach (var rm in rules)
        {
            var r = (ApprovalRouteRule)rm;
            if (r.IsActive.Value != true) continue;
            if (r.ExpenseCategorySel.Value != null && !IsSameId(r.ExpenseCategorySel.Value, catId)) continue;
            var min = r.MinAmount.Value ?? 0;
            if (amount < min) continue;
            var max = r.MaxAmount.Value;
            if (max != null && amount > max) continue;
            matched = r.TemplateSel.Value;
            break;
        }
        if (matched == null)
        {
            var cat = FindCategory(catId);
            var nm = cat == null ? "" : $"{cat.Name.Value}";
            Toaster.Error($"承認ルート判定に一致するルールがありません（費目 {nm} / 判定額 {amount:#,0} 円）。システム管理 > 承認/承認ルート判定 を確認してください");
            return new List<object>();
        }
        if (!ContainsId(result, matched)) result.Add(matched);
    }
    return result;
}

// ID の等値判定。SelectField 由来で値の型（string/decimal）が揃わないことがあるため文字列正規化で比較
bool IsSameId(object a, object b)
{
    return $"{a}" == $"{b}";
}

bool ContainsId(List<object> list, object id)
{
    foreach (var x in list)
    {
        if (IsSameId(x, id)) return true;
    }
    return false;
}

// 承認ルートの判定額 = 明細合計（立替・事前を問わない）。
// 事前申請でも申請時に明細（費目と見込み金額）を入れる規約にしたため、区別が要らなくなった。
int GetJudgeAmount()
{
    return Amount.Value ?? 0;
}

// 申請前の業務チェック (ApprovalFlow の申請/再申請から呼ばれる契約メソッド)
bool ValidateForApply()
{
    if (this.ValidateInput() != true)
    {
        Toaster.Error("入力内容を確認してください");
        return false;
    }
    if (PayeeType.Value == "partner" && PayeePartner.Value == null)
    {
        Toaster.Error("支払取引先を選択してください");
        return false;
    }
    if (PayeeType.Value != "partner" && PayeeUser.Value == null)
    {
        Toaster.Error("精算対象者を選択してください");
        return false;
    }

    // 入力欄に書きかけが残っていたら、追加し忘れとして止める（黙って捨てない）
    if (HasPendingEntry())
    {
        Toaster.Error("入力欄に、明細に追加していない内容が残っています。「この内容で明細に追加」（または「この明細を更新」）を押すか、「入力内容をクリア」（編集中なら「編集をやめる」）で消してください");
        return false;
    }

    var lines = GetLines();
    if (lines.Count == 0)
    {
        Toaster.Error("明細を 1 件以上追加してください（レシート 1 枚が 1 件です）");
        return false;
    }

    var no = 0;
    var total = 0;
    var missingReceipt = 0;
    foreach (var l in lines)
    {
        no = no + 1;
        if (l.Amount.Value == null || l.Amount.Value <= 0)
        {
            Toaster.Error($"{no} 行目: 金額（税込）を入力してください");
            return false;
        }
        if (l.UsedDate.Value == null)
        {
            Toaster.Error($"{no} 行目: 利用日を入力してください");
            return false;
        }
        var cat = FindCategory(l.ExpenseCategoryRef.Value);
        if (cat == null)
        {
            Toaster.Error($"{no} 行目: 費目を選択してください");
            return false;
        }
        if (cat.IsEntertainment.Value == true)
        {
            var guestOk = !string.IsNullOrEmpty(l.EntertainmentGuest.Value);
            var countOk = (l.EntertainmentCount.Value ?? 0) > 0;
            var purposeOk = !string.IsNullOrEmpty(l.EntertainmentPurpose.Value);
            if (!guestOk || !countOk || !purposeOk)
            {
                Toaster.Error($"{no} 行目: 交際費は相手先・参加人数・目的の入力が必須です");
                return false;
            }
        }
        if (!ValidateLineTax(l, cat, no)) return false;
        total = total + l.Amount.Value;
        if (l.Receipt.FileName == null || l.Receipt.FileName == "") missingReceipt = missingReceipt + 1;
    }

    if (total <= 0)
    {
        Toaster.Error("金額を入力してください");
        return false;
    }
    if (ExpenseDate.Value == null)
    {
        Toaster.Error("計上日を入力してください");
        return false;
    }

    // 領収書の未添付警告（U2-6: 申請はブロックしない。添付できない実務ケースを許容）
    if (missingReceipt > 0)
    {
        Toaster.Warn($"領収書が添付されていない明細が {missingReceipt} 行あります。紙の原本を保管してください（申請後は添付できません）");
    }
    return true;
}

// 手入力の「うち消費税」の検算（行単位・ADR-0051 の判定を明細に適用）。
// 明白な誤り（負値・税込金額以上）は止め、税率からの計算値との乖離は警告で通す
// （軽減税率・非課税品の混在で数円〜数十円ずれるのは正常）
bool ValidateLineTax(ExpenseRequestLine l, ExpenseCategory cat, int no)
{
    var manual = l.TaxAmount.Value ?? 0;
    if (manual == 0) return true;
    if (manual < 0)
    {
        Toaster.Error($"{no} 行目: 「うち消費税」に負の金額は入力できません");
        return false;
    }
    var gross = l.Amount.Value ?? 0;
    if (gross <= 0) return true;
    if (manual >= gross)
    {
        Toaster.Error($"{no} 行目: 「うち消費税」{manual:#,0} 円が金額（税込）{gross:#,0} 円以上です。税込金額に含まれる消費税額を入力してください");
        return false;
    }
    var tcat = ResolveLineTaxCategory(l, cat);
    if (!IsTaxablePurchaseTaxCategory(tcat)) return true;
    decimal pct = GetTaxRatePercent(tcat);
    if (pct == 0) return true;
    int theory = gross * pct / (100 + pct);
    if (theory <= 0) return true;
    var tolerance = Math.Max(theory * GetTaxDiffTolerancePercent() / 100, 1);
    if (Math.Abs(manual - theory) > tolerance)
    {
        Toaster.Warn($"{no} 行目: 「うち消費税」{manual:#,0} 円は税率 {pct:0.#}% での計算値 {theory:#,0} 円と離れています。桁の間違いがないか確認してください（軽減税率や非課税品が混ざったレシートなら問題ありません）");
    }
    return true;
}

// 手入力の「うち消費税」を疑う幅（税率からの計算値に対する %）。
// 桁ミス（10 倍・1/10）は確実に捕まえ、軽減税率や非課税品が混ざったレシートの端数ブレは通す帯。
// 利用者が調整する類の値ではないためマスタ化せずここに置く（2026-08-12 合意）
int GetTaxDiffTolerancePercent()
{
    return 10;
}

void OnAfterInitialization()
{
    if (IsNewData)
    {
        // 新規時: ApprovalFlow を初期化。this.Id.Value は @temporary:guid だが、
        // CLB の TemporaryIdResolver が双方向サイクルを自動解決する。
        // テンプレートは申請時に明細から解決するため、ここでは設定しない（ADR-0066）
        ApprovalFlow.ChildModule.Initialize("ExpenseRequest", this.Id.Value);

        // 既定値: 立替精算 / 社員へ精算（対象者=本人） / 精算ステータス=下書き
        RequestType.Value = "reimburse";
        PayeeType.Value = "employee";
        PayeeUser.Value = CurrentUser.Id.Value;
        SettlementStatus.Value = "draft";
        ExpenseDate.Value = DateOnly.FromDateTime(DateTime.Today);
        // 複製は保存済みの申請に対する操作（未保存では出さない。驚き最小: 2026-08-03 UXレビュー）
        DuplicateButton.IsVisible = false;
        UpdateVisibility();
        UpdateAccountingButtons();
        UpdateLineButtons();
        return;
    }

    // 申請後（フロー進行中/完了）は申請内容を変更不可。
    // 下書き（未申請の複製ドラフト／却下・キャンセルで差し戻し済み）は編集可。
    EditableGrid.IsEnabled = (SettlementStatus.Value == "draft");
    UpdateVisibility();
    UpdateAccountingButtons();
    UpdateLineButtons();
}

// ============================================================
// 精算ステータスと経理処理 (B2-4)
// draft → applying(申請) → approved(承認完了) → accounting(仕訳生成)
//       → settled(精算=支払済) → completed(完了)。前半はフロー連動、後半は経理操作。
// ============================================================

// ApprovalFlow からの状態変化通知 (契約メソッド。親 Submit の直前に呼ばれる)
void OnApprovalFlowStatusChanged(string flowStatus)
{
    if (flowStatus == "Pending")
    {
        SettlementStatus.Value = "applying";
        // 部門スナップショット: 申請時の申請者の所属部を記録（U2-8。ADR-0044。再申請時は初回の値を保持）
        if (DepartmentRef.Value == null)
        {
            DepartmentRef.Value = CurrentUser.所属部.Value;
        }
        // 見込み額スナップショット: 事前申請は「申請したときの明細合計」を記録し、実費との比較に使う。
        // 実費確定後の再承認では上書きしない（比較の基準が動いてしまうため）
        if (RequestType.Value == "advance" && ActualConfirmed.Value != true)
        {
            EstimatedAmount.Value = Amount.Value;
        }
    }
    else if (flowStatus == "Approved")
    {
        // 経理処理以降へ進んでいる場合は巻き戻さない
        var st = SettlementStatus.Value;
        if (st == null || st == "" || st == "draft" || st == "applying") SettlementStatus.Value = "approved";
    }
    else if (flowStatus == "Rejected" || flowStatus == "Cancelled")
    {
        SettlementStatus.Value = "draft";
    }
    UpdateAccountingButtons();
}

// 経理ボタンと精算ステータス表示の出し分け
// 会計処理（仕訳生成・精算・完了）は経理専用（B-8）。
// 実費確定は申請者本人が行う業務のため全ユーザーに出す（ゲートしない）。
void UpdateAccountingButtons()
{
    var st = SettlementStatus.Value;
    var isAccounting = CurrentUser.HasAccountingAccess.Value == true;
    SettlementStatusLabel.IsVisible = !IsNewData;
    SettlementStatus.IsVisible = !IsNewData;

    // 事前申請は承認後に実費（＝明細）を確定してから仕訳生成に進む
    var isAdvance = (RequestType.Value == "advance");
    var needsActual = !IsNewData && (st == "approved") && isAdvance && (ActualConfirmed.Value != true);
    ActualAmountLabel.IsVisible = needsActual;
    ConfirmActualButton.IsVisible = needsActual;

    // 明細リストの操作（削除）は編集できる状態のあいだだけ
    Lines.IsEnabled = CanEditLines();

    GenerateJournalButton.IsVisible = isAccounting && !IsNewData && (st == "approved") && !needsActual;
    SettleButton.IsVisible = isAccounting && !IsNewData && (st == "accounting");
    CompleteButton.IsVisible = isAccounting && !IsNewData && (st == "settled");

    // 削除は「起案者本人 かつ 精算=下書き」のみ（2026-07-16 ユーザー決定）
    DeleteDraftButton.IsVisible = !IsNewData && (st == "draft") && IsSameId(Creator.Value, CurrentUser.Id.Value);
}

// 下書きの削除（本人・下書きのみ。確認ダイアログ付き）
void DeleteDraft_OnClick()
{
    if (SettlementStatus.Value != "draft") { Toaster.Error("下書きの申請のみ削除できます"); return; }
    if (!IsSameId(Creator.Value, CurrentUser.Id.Value)) { Toaster.Error("自分が起案した申請のみ削除できます"); return; }
    var result = MessageBox.Show($"下書き「{Title.Value}」を明細ごと削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;

    using var loading = LoadingService.StartLoading(0);

    // 入力欄の行は親を持たない（expense_request_id が空）ので DeleteTogether では消えない。
    // 親を消す前に自分で片付ける
    var entryId = EditingLineIdRaw.Value;
    if (entryId != null)
    {
        var es = new ModuleSearcher<ExpenseRequestLine>();
        es.AddEquals(l => l.Id.Value, entryId);
        var entry = es.ExecuteFirstOrDefault();
        if (entry != null)
        {
            EditingLineIdRaw.Value = null;
            this.Submit();
            var retEntry = ((ExpenseRequestLine)entry).Delete();
            if (retEntry != true) { Logger.Warn($"入力欄の行（id={entryId}）を片付けられませんでした"); }
        }
    }

    // 既知の限界: 承認フロー（子）の行はスクリプトから物理削除できず孤児として残る
    // （実測 2026-07-16）。明細は ListField の DeleteTogether で親と一緒に消える
    this.Delete();
    Toaster.Success("下書きを削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("ExpenseRequest"));
}

// 事前申請の実費確定: 明細を実費に直したうえで押す。
// 見込みとの乖離が大きければ再承認、問題なければそのまま経理処理へ。
// 超過判定: (a) 承認ルートが変わる (b) 実費 > 見込み × EXP_OVERRUN_RATE(%)
void ConfirmActual_OnClick()
{
    if (SettlementStatus.Value != "approved" || RequestType.Value != "advance") return;
    if (ActualConfirmed.Value == true) return;

    if (HasPendingEntry())
    {
        Toaster.Error("入力欄に、明細に追加していない内容が残っています。「この内容で明細に追加」（または「この明細を更新」）を押すか、「入力内容をクリア」（編集中なら「編集をやめる」）で消してください");
        return;
    }

    var lines = GetLines();
    if (lines.Count == 0) { Toaster.Error("実費の明細を入力してください"); return; }
    var no = 0;
    foreach (var l in lines)
    {
        no = no + 1;
        if (l.Amount.Value == null || l.Amount.Value <= 0)
        {
            Toaster.Error($"{no} 行目: 実費（税込）を入力してください");
            return;
        }
        if (!ValidateLineTax(l, FindCategory(l.ExpenseCategoryRef.Value), no)) return;
    }

    var actual = Amount.Value ?? 0;
    if (actual <= 0) { Toaster.Error("実費（税込）を入力してください"); return; }
    var estimated = EstimatedAmount.Value ?? 0;

    // ルートが変わったかは「いま必要な承認者の段構成」と「実際に承認された段構成」を突き合わせて判定する。
    // 金額区分を跨いだ場合だけでなく、実費確定のあいだに交際費の行が足されたようなケースも捕まえられる
    // （明細を編集できる期間があるため、金額だけの比較では取りこぼす）
    var required = SelectTemplateIds();
    if (required.Count == 0) return;
    var crossed = ApprovalFlow.ChildModule.IsRouteChanged(required);
    ActualConfirmed.Value = true;

    var overRate = GetThresholdAmountAt("EXP_OVERRUN_RATE", ExpenseDate.Value);
    var overLimit = (overRate > 0) && (actual * 100 > estimated * overRate);

    if (crossed || overLimit)
    {
        // 再承認: フローを Pending に戻し実費でルート再解決（精算ステータスは通知で applying に戻る）
        ApprovalFlow.ChildModule.ReapproveForOverrun($"実費 {actual:#,0} 円が見込み {estimated:#,0} 円を超過したため再承認");
    }
    else
    {
        var ret = this.Submit();
        if (ret != true) { Toaster.Error("実費の保存に失敗しました"); ActualConfirmed.Value = false; return; }
        Toaster.Success($"実費 {actual:#,0} 円を確定しました。仕訳を生成できます");
    }
    UpdateAccountingButtons();
}

// 経理: 仕訳を生成 (approved → accounting)
// D: 明細ごとに費目の既定勘定科目（固定資産計上の行は工具器具備品1520）＋その行の仮払消費税行
// C: 未払金2020 を 1 行（合計）
void GenerateJournal_OnClick()
{
    if (SettlementStatus.Value != "approved") { Toaster.Error("承認済の申請のみ仕訳を生成できます"); return; }
    if (ExpenseDate.Value == null) { Toaster.Error("計上日が入力されていません"); return; }

    var lines = GetLinesFromDb();
    if (lines.Count == 0) { Toaster.Error("明細がありません"); return; }

    // 確定仕訳を作る操作で、この画面に取り消す導線が無い＝不可逆なので確認する（ADR-0062）
    var answer = MessageBox.Show(
        $"この申請の明細 {lines.Count} 行から未払計上の仕訳を生成します。生成した仕訳を取り消す導線はこの画面にありません"
        + "（誤りは振替伝票側で訂正してください）。よろしいですか？",
        "生成する", "キャンセル");
    if (answer != "生成する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "expense");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) { Toaster.Error("この申請の仕訳は既に生成済みです"); return; }

    // 会計年度の解決と締め済み期間ガード（境界日知見: 月末日は辞書順比較で失敗するため月初日で解決）
    // 計上日の期間が締め済み（または期間未設定）なら処理日（今日）へ自動フォールバックする
    var entryDate = ExpenseDate.Value;
    var usedFallback = false;
    var typedFy = ResolveYearForDate(entryDate);
    var typedPeriod = ResolvePeriodForDate(entryDate);
    var origClosed = (typedPeriod != null && typedPeriod.Status.Value == "closed");
    if (typedFy == null || typedPeriod == null || origClosed)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var fyToday = ResolveYearForDate(today);
        var periodToday = ResolvePeriodForDate(today);
        if (fyToday == null || periodToday == null || periodToday.Status.Value == "closed")
        {
            Toaster.Error("計上日の期間に起票できず、本日の期間も締め済みまたは未設定です。会計年度・月次期間の設定を確認してください");
            return;
        }
        if (typedFy != null && fyToday.Id.Value != typedFy.Id.Value)
        {
            Toaster.Warn($"計上日（{ExpenseDate.Value:yyyy/MM/dd}）は前年度です。当期の費用として計上します。金額が重要な場合は決算修正をご検討ください");
        }
        entryDate = today;
        typedFy = fyToday;
        typedPeriod = periodToday;
        usedFallback = true;
    }

    // 貸方科目: 未払金(2020) / 税行科目: 仮払消費税(1900)
    var apS = new ModuleSearcher<Account>();
    apS.AddIn(e => e.Code.Value, "2020", "1900");
    var settleAccounts = apS.Execute();
    object apAccountId = null;
    object purchaseTaxAccountId = null;
    foreach (var a in settleAccounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "2020") { apAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1900") { purchaseTaxAccountId = acc.Id.Value; }
    }
    if (apAccountId == null) { Toaster.Error("未払金(2020)の科目がありません"); return; }

    // 固定資産計上の行があるときだけ工具器具備品(1520)を解決する
    object assetAccountId = null;
    var hasAssetLine = false;
    foreach (var l in lines) { if (l.IsFixedAsset.Value == true) hasAssetLine = true; }
    if (hasAssetLine)
    {
        var accS = new ModuleSearcher<Account>();
        accS.AddEquals(e => e.Code.Value, "1520");
        var assetAcc = accS.ExecuteFirstOrDefault();
        if (assetAcc == null) { Toaster.Error("工具器具備品(1520)の科目がありません"); return; }
        // この科目はそのまま固定資産台帳の計上科目になる。固定資産科目でないと償却仕訳を
        // 生成できない（ADR-0063 の関門で止まる）ので、先にここで気づかせる
        if (((Account)assetAcc).IsFixedAssetAccount.Value != true)
        {
            Toaster.Error("工具器具備品(1520)が「固定資産科目」になっていません。科目マスタを確認してください");
            return;
        }
        assetAccountId = ((Account)assetAcc).Id.Value;
    }

    // 申請者の所属部門を解決して全行に引き継ぐ（部門別予実に乗せる。B-7）
    // 注: レイアウトに出ていない Creator は遅延ロードで null のことがある (#60) → DB から取り直す
    var creatorId = Creator.Value;
    if (creatorId == null)
    {
        var es = new ModuleSearcher<ExpenseRequest>();
        es.AddEquals(e => e.Id.Value, this.Id.Value);
        var self = es.ExecuteFirstOrDefault();
        if (self != null) { creatorId = ((ExpenseRequest)self).Creator.Value; }
    }
    object creatorDeptId = null;
    if (creatorId != null)
    {
        var us = new ModuleSearcher<AppUser>();
        us.AddEquals(u => u.Id.Value, creatorId);
        var creatorUser = us.ExecuteFirstOrDefault();
        if (creatorUser != null) { creatorDeptId = ((AppUser)creatorUser).所属部.Value; }
    }

    // ---- 明細から仕訳行を組み立てる ----
    // 借方は明細 1 行につき 1 行（同じ科目でも畳まない。総勘定元帳で明細が追えるほうが実務で役に立つ）。
    // 税行は借方行ごと（レシート記載の税額が正なので、1 枚ごとの税額をそのまま記帳する）。
    // 貸方（未払金）は支払先がヘッダで 1 つなので 1 行にまとめる。
    var dcList = new List<string>();
    var accList = new List<object>();
    var taxCatList = new List<object>();
    var taxModeList = new List<string>();
    var amtList = new List<int>();
    var inAmtList = new List<int>();
    var descList = new List<string>();
    var prjList = new List<object>();
    var isTaxList = new List<bool>();
    var parentNoList = new List<int>();

    var total = 0;
    var lineNo = 0;
    var summaryName = "";
    foreach (var l in lines)
    {
        var cat = FindCategory(l.ExpenseCategoryRef.Value);
        if (cat == null) { Toaster.Error($"{lineNo + 1} 行目: 費目が解決できません"); return; }
        var debitAccountId = cat.DefaultAccount.Value;
        if (l.IsFixedAsset.Value == true) { debitAccountId = assetAccountId; }
        if (debitAccountId == null) { Toaster.Error($"費目「{cat.Name.Value}」に既定勘定科目が設定されていません"); return; }

        var tcat = ResolveLineTaxCategory(l, cat);
        object taxCatId = null;
        if (tcat != null) taxCatId = tcat.Id.Value;
        if (taxCatId == null) { Toaster.Error($"{lineNo + 1} 行目: 税区分が解決できません（費目の既定税区分を確認してください）"); return; }

        int gross = l.Amount.Value ?? 0;
        int tax = CalcLineTax(l);
        int baseAmount = gross - tax;
        total = total + gross;

        var desc = l.Description.Value;
        if (string.IsNullOrEmpty(desc)) desc = $"{Title.Value}";
        if (!string.IsNullOrEmpty(l.UsedAt.Value)) desc = $"{desc}（{l.UsedAt.Value}）";

        lineNo = lineNo + 1;
        var debitNo = lineNo;
        dcList.Add("D");
        accList.Add(debitAccountId);
        taxCatList.Add(taxCatId);
        taxModeList.Add("inclusive");
        amtList.Add(baseAmount);
        inAmtList.Add(gross);
        descList.Add(desc);
        prjList.Add(l.ProjectRef.Value);
        isTaxList.Add(false);
        parentNoList.Add(0);

        if (tax > 0)
        {
            lineNo = lineNo + 1;
            dcList.Add("D");
            accList.Add(purchaseTaxAccountId);
            taxCatList.Add(taxCatId);
            taxModeList.Add("none");
            amtList.Add(tax);
            inAmtList.Add(tax);
            descList.Add($"消費税（行{debitNo}）");
            prjList.Add(l.ProjectRef.Value);
            isTaxList.Add(true);
            parentNoList.Add(debitNo);
        }

        if (summaryName == "")
        {
            var accName = ResolveAccountName(debitAccountId);
            summaryName = accName;
            if (accName != $"{cat.Name.Value}") summaryName = $"{accName}（費目: {cat.Name.Value}）";
        }
    }

    if (total <= 0) { Toaster.Error("金額が入力されていません"); return; }
    if (purchaseTaxAccountId == null)
    {
        foreach (var b in isTaxList) { if (b) { Toaster.Error("仮払消費税(1900)の科目がありません"); return; } }
    }

    lineNo = lineNo + 1;
    dcList.Add("C");
    accList.Add(apAccountId);
    taxCatList.Add(null);
    taxModeList.Add("none");
    amtList.Add(total);
    inAmtList.Add(total);
    descList.Add($"{Title.Value}");
    prjList.Add(null);
    isTaxList.Add(false);
    parentNoList.Add(0);

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

    var je = new JournalEntry();
    je.EntryDate.Value = entryDate;
    je.EntryType.Value = "auto";
    if (usedFallback) { je.Description.Value = $"経費精算 {Title.Value}（計上日 {ExpenseDate.Value:yyyy/MM/dd}）"; }
    else { je.Description.Value = $"経費精算 {Title.Value}"; }
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "expense";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(dcList.Count);
    var idx = -1;
    foreach (var row in je.Lines.Rows)
    {
        var jl = (JournalLine)row;
        idx = idx + 1;
        jl.LineNo.Value = idx + 1;
        jl.Dc.Value = dcList[idx];
        jl.Account.Value = accList[idx];
        jl.TaxInputMode.Value = taxModeList[idx];
        jl.Amount.Value = amtList[idx];
        jl.InputAmount.Value = inAmtList[idx];
        jl.Description.Value = descList[idx];
        if (taxCatList[idx] != null) { jl.TaxCategory.Value = taxCatList[idx]; }
        if (creatorDeptId != null) { jl.Department.Value = creatorDeptId; }
        if (prjList[idx] != null) { jl.ProjectRef.Value = prjList[idx]; }
        if (isTaxList[idx])
        {
            jl.IsTaxLine.Value = true;
            jl.ParentLineNo.Value = parentNoList[idx];
        }
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("仕訳の生成に失敗しました"); return; }

    // 固定資産計上対象の行を台帳へ自動登録（取得価額は税抜本体額。部門は仕訳と同じ）
    var assetNo = 0;
    var registered = 0;
    foreach (var l in lines)
    {
        assetNo = assetNo + 1;
        if (l.IsFixedAsset.Value != true) continue;
        int gross = l.Amount.Value ?? 0;
        int baseAmount = gross - CalcLineTax(l);
        if (RegisterFixedAsset(l, assetNo, assetAccountId, baseAmount, creatorDeptId)) registered = registered + 1;
    }

    SettlementStatus.Value = "accounting";
    var ret2 = this.Submit();
    if (ret2 == false) { Toaster.Error("精算ステータスの更新に失敗しました"); return; }
    UpdateAccountingButtons();
    Toaster.Success($"仕訳 No.{nextNo} を生成しました（明細 {lines.Count} 行 / 借方 {summaryName} ほか / 貸方 未払金 {total:#,0} 円）");
    if (registered > 0)
    {
        Toaster.Info($"固定資産台帳に {registered} 件を登録しました（耐用年数・償却方法は台帳で確定してください）");
    }
    if (usedFallback)
    {
        Toaster.Info($"計上日（{ExpenseDate.Value:yyyy/MM/dd}）の期間が締め済みのため、本日（{entryDate:yyyy/MM/dd}）日付で起票しました（摘要に計上日を記載）");
    }
}

// 勘定科目名（解決できなければ空文字）
string ResolveAccountName(object accountId)
{
    if (accountId == null) return "";
    var s = new ModuleSearcher<Account>();
    s.AddEquals(e => e.Id.Value, accountId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return "";
    return $"{((Account)found).Name.Value}";
}

// この申請を複製: 反復的な経費（定期券・毎月の会費など）を過去申請から新規作成する。
// 明細もそのまま複製する。コピーしないもの: 利用日(=今日)・領収書・承認履歴・精算ステータス(=下書き)
void Duplicate_OnClick()
{
    if (this.IsNewData) { Toaster.Error("保存済みの申請のみ複製できます"); return; }

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var srcLines = GetLinesFromDb();

    var copy = new ExpenseRequest();
    copy.Title.Value = Title.Value;
    copy.Purpose.Value = Purpose.Value;
    copy.RequestType.Value = RequestType.Value;
    copy.PayeeType.Value = PayeeType.Value;
    copy.PayeePartner.Value = PayeePartner.Value;
    copy.Amount.Value = Amount.Value;
    copy.TaxAmount.Value = TaxAmount.Value;
    copy.ExpenseDate.Value = DateOnly.FromDateTime(DateTime.Today);
    copy.PayeeUser.Value = CurrentUser.Id.Value;
    copy.SettlementStatus.Value = "draft";
    var ret = copy.Submit();
    if (ret != true) { Toaster.Error("複製に失敗しました"); return; }

    // 作成した複製を DB から取り直す（Submit 後の Id はテンポラリの可能性があるため）
    var s = new ModuleSearcher<ExpenseRequest>();
    s.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    s.OrderByDescending(e => e.Id.Value);
    s.Limit(1);
    var created = s.ExecuteFirstOrDefault();
    if (created == null) { Toaster.Error("複製の取得に失敗しました"); return; }
    var typedCreated = (ExpenseRequest)created;

    // 明細を複製（領収書の添付は引き継がない＝レシートは都度の実物が正）
    var today = DateOnly.FromDateTime(DateTime.Today);
    var n = 0;
    foreach (var l in srcLines)
    {
        n = n + 1;
        var nl = new ExpenseRequestLine();
        nl.ExpenseRequestId.Value = typedCreated.Id.Value;
        nl.LineNo.Value = n;
        nl.UsedDate.Value = today;
        nl.ExpenseCategoryRef.Value = l.ExpenseCategoryRef.Value;
        nl.TaxCategoryRef.Value = l.TaxCategoryRef.Value;
        nl.Amount.Value = l.Amount.Value;
        nl.TaxAmount.Value = l.TaxAmount.Value;
        nl.ProjectRef.Value = l.ProjectRef.Value;
        nl.UsedAt.Value = l.UsedAt.Value;
        nl.Description.Value = l.Description.Value;
        nl.EntertainmentGuest.Value = l.EntertainmentGuest.Value;
        nl.EntertainmentCount.Value = l.EntertainmentCount.Value;
        nl.EntertainmentPurpose.Value = l.EntertainmentPurpose.Value;
        nl.IsFixedAsset.Value = l.IsFixedAsset.Value;
        nl.Submit();
    }

    // 承認フローの行を Draft で作成し、FK（approval_flow_id）を複製に張る。
    // 子行が無い親は CLB が子モジュールを実体化せず申請ボタンが出ない（2026-07-08 実測）。
    var flow = new ApprovalFlow();
    flow.Status.Value = "Draft";
    flow.AttemptNo.Value = 1;
    flow.ParentModuleName.Value = "ExpenseRequest";
    flow.ParentId.Value = $"{typedCreated.Id.Value}";
    var retFlow = flow.Submit();
    if (retFlow != true) { Toaster.Error("承認フローの初期化に失敗しました"); return; }

    var fs = new ModuleSearcher<ApprovalFlow>();
    fs.OrderByDescending(f => f.Id.Value);
    fs.Limit(1);
    var newFlow = fs.ExecuteFirstOrDefault();
    if (newFlow != null)
    {
        typedCreated.ApprovalFlowIdRaw.Value = ((ApprovalFlow)newFlow).Id.Value;
        typedCreated.Submit();
    }

    Toaster.Success($"申請を複製しました（明細 {n} 行）。利用日・金額を確認して申請してください");
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("ExpenseRequest", $"{typedCreated.Id.Value}"));
}

// 月初日で年度を解決（境界日の罠回避）。該当なしは null
FiscalYear ResolveYearForDate(var d)
{
    var first = new DateOnly(d.Year, d.Month, 1);
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, first);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, first);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalYear)found;
}

// 月初日で月次期間を解決（境界日の罠回避）。該当なしは null
FiscalPeriod ResolvePeriodForDate(var d)
{
    var first = new DateOnly(d.Year, d.Month, 1);
    var s = new ModuleSearcher<FiscalPeriod>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, first);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, first);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalPeriod)found;
}

// 税区分に紐づく税率(%)。未設定・解決不能なら 0
decimal GetTaxRatePercent(TaxCategory tcat)
{
    if (tcat == null) return 0;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    return ((TaxRate)foundRate).RatePercent.Value ?? 0;
}

// 固定資産台帳への自動登録（償却方法は仮=定額法。耐用年数と方法は経理が台帳で確定する）
bool RegisterFixedAsset(ExpenseRequestLine l, int lineNo, object assetAccountId, int baseAmount, object departmentId)
{
    var code = l.AssetNo.Value;
    if (code == null || code == "") { code = $"EXP-{this.Id.Value}-{lineNo}"; }
    var fs = new ModuleSearcher<FixedAsset>();
    fs.AddEquals(f => f.Code.Value, code);
    if (fs.Execute().Count > 0) return false;

    var name = l.Description.Value;
    if (string.IsNullOrEmpty(name)) name = $"{Title.Value}";

    var fa = new FixedAsset();
    fa.Code.Value = code;
    fa.Name.Value = name;
    if (departmentId != null) { fa.Department.Value = departmentId; }
    fa.AssetAccount.Value = assetAccountId;
    fa.AcquisitionDate.Value = l.UsedDate.Value;
    fa.AcquisitionCost.Value = baseAmount;
    fa.DepreciationMethod.Value = "straight_line";
    fa.Status.Value = "in_use";
    fa.Memo.Value = $"経費申請「{Title.Value}」{lineNo} 行目から自動登録。耐用年数・償却方法を確認してください";
    var ret = fa.Submit();
    if (ret != true) { Toaster.Error($"固定資産台帳への自動登録に失敗しました（{code}）。手動で登録してください"); return false; }
    return true;
}

// 経理: 精算済にする (accounting → settled)
// B-6: 支払仕訳 (D 未払金2020 / C 普通預金1020) を生成してからステータスを進める
void Settle_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("精算（支払仕訳の生成）は経理のみ実行できます");
        return;
    }
    if (SettlementStatus.Value != "accounting") return;
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("金額が入力されていません"); return; }

    // 支払仕訳を作る操作で、この画面に取り消す導線が無い＝不可逆なので確認する（ADR-0062）
    var answer = MessageBox.Show(
        "この申請を精算済みにし、支払仕訳を生成します。取り消す導線はこの画面にありません"
        + "（誤りは振替伝票側で訂正してください）。よろしいですか？",
        "精算する", "キャンセル");
    if (answer != "精算する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "expense_payment");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) { Toaster.Error("この申請の支払仕訳は既に生成済みです"); return; }

    // 支払日=今日。会計年度・期間の解決 (境界日知見: 期間解決はその月の月初日で行う)
    var payDate = DateOnly.FromDateTime(DateTime.Today);
    var monthFirst = new DateOnly(payDate.Year, payDate.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("支払日に対応する会計年度がありません"); return; }
    var typedFy = (FiscalYear)fy;
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { Toaster.Error("支払日に対応する月次期間がありません"); return; }
    var typedPeriod = (FiscalPeriod)period;
    if (typedPeriod.Status.Value == "closed") { Toaster.Error("支払日の期間は締め済みです"); return; }

    // 科目解決: 未払金2020 / 普通預金1020
    var accS = new ModuleSearcher<Account>();
    accS.AddIn(e => e.Code.Value, "2020", "1020");
    var accounts = accS.Execute();
    object apAccountId = null;
    object bankAccountId = null;
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        if (acc.Code.Value == "2020") { apAccountId = acc.Id.Value; }
        if (acc.Code.Value == "1020") { bankAccountId = acc.Id.Value; }
    }
    if (apAccountId == null) { Toaster.Error("未払金(2020)の科目がありません"); return; }
    if (bankAccountId == null) { Toaster.Error("普通預金(1020)の科目がありません"); return; }

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

    int amount = Amount.Value;

    // 支払仕訳: D 未払金 / C 普通預金
    var je = new JournalEntry();
    je.EntryDate.Value = payDate;
    je.EntryType.Value = "auto";
    je.Description.Value = $"経費支払 {Title.Value}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "expense_payment";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(2);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = $"経費支払 {Title.Value}";
        l.TaxInputMode.Value = "none";
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = apAccountId;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = bankAccountId;
        }
    }
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("支払仕訳の生成に失敗しました"); return; }

    SettlementStatus.Value = "settled";
    var ret2 = this.Submit();
    if (ret2 != true) { Toaster.Error("精算ステータスの更新に失敗しました（支払仕訳は生成済みです）"); return; }
    UpdateAccountingButtons();
    Toaster.Success($"支払仕訳 No.{nextNo}（{amount:#,0} 円）を生成し精算済にしました");
}

// 経理: 完了にする (settled → completed)
void Complete_OnClick()
{
    if (SettlementStatus.Value != "settled") return;
    SettlementStatus.Value = "completed";
    var ret = this.Submit();
    if (ret != true) { Toaster.Error("更新に失敗しました"); SettlementStatus.Value = "settled"; return; }
    UpdateAccountingButtons();
    Toaster.Success("完了にしました");
}

void RequestType_OnDataChanged()
{
    UpdateVisibility();
}

void PayeeType_OnDataChanged()
{
    UpdateVisibility();
}

// 申請区分・支払先区分に応じた項目の出し分け（費目まわりの出し分けは明細側へ移った）
void UpdateVisibility()
{
    // 見込み額: 事前申請のみ（申請時の明細合計が記録される読み取り専用の値）
    var isAdvance = (RequestType.Value == "advance");
    EstimatedAmountLabel.IsVisible = isAdvance;
    EstimatedAmount.IsVisible = isAdvance;

    // 支払先: 社員へ精算 ⇔ 取引先へ支払
    var toPartner = (PayeeType.Value == "partner");
    PayeeUserLabel.IsVisible = !toPartner;
    PayeeUser.IsVisible = !toPartner;
    PayeePartnerLabel.IsVisible = toPartner;
    PayeePartner.IsVisible = toPartner;
}

// system_thresholds から指定コードの閾値を、指定日で期間解決して取得（該当なしは 0）
int GetThresholdAmountAt(string code, var d)
{
    var s = new ModuleSearcher<SystemThreshold>();
    var thresholds = s.Execute();
    var limit = 0;
    foreach (var t in thresholds)
    {
        var th = (SystemThreshold)t;
        if (th.Code.Value != code) continue;
        if (d != null && th.ValidFrom.Value != null && d < th.ValidFrom.Value) continue;
        if (d != null && th.ValidTo.Value != null && d > th.ValidTo.Value) continue;
        limit = th.Amount.Value ?? 0;
    }
    return limit;
}
