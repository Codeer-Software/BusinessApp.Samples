// FixedAsset.mod.cs — 固定資産台帳
// 責務: 取得価額×取得日から少額判定（system_thresholds を期間解決）して処理方法を提案 /
//        対象年度の減価償却仕訳（entry_type=auto, source_type='depreciation'）を自動生成 /
//        **除却・売却の仕訳生成と取消**（BUG-0095・ADR-0070）
// 設計: docs/04 §7。簿価は理論値ベースの年次償却（月割は取得年度のみ）。残存簿価1円。
//
// 【償却は直接法】借方 減価償却費 / 貸方 資産科目。累計額勘定は使わない。
//   したがって帳簿価額 = 取得価額 − この資産の償却仕訳の合計 で求まる。
// 【処分の作法】状態を変える入口はボタンに一本化する（ADR-0026/0070）。状態欄は読み取り専用。
//   締め前なら取り消せる／締め済みなら取り消せない（当期に反対仕訳を起こす）＝ ADR-0070。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "in_use";
        DepreciationMethod.Value = "straight_line";
    }
    UpdateMethodHint();
    UpdateDisposalUi();
}

void AcquisitionCost_OnDataChanged()
{
    UpdateMethodHint();
}

void AcquisitionDate_OnDataChanged()
{
    UpdateMethodHint();
}

// 少額判定: 取得日時点の制度閾値マスタで処理方法を提案する
void UpdateMethodHint()
{
    MethodHint.Value = "";
    if (AcquisitionCost.Value == null || AcquisitionDate.Value == null) return;

    var s = new ModuleSearcher<SystemThreshold>();
    var thresholds = s.Execute();

    int cost = AcquisitionCost.Value;
    var d = AcquisitionDate.Value;
    int smallLimit = 0;
    int lumpLimit = 0;
    int smeLimit = 0;
    foreach (var t in thresholds)
    {
        var th = (SystemThreshold)t;
        if (th.ValidFrom.Value != null && d < th.ValidFrom.Value) continue;
        if (th.ValidTo.Value != null && d > th.ValidTo.Value) continue;
        if (th.Code.Value == "SMALL_ASSET_EXPENSE") { smallLimit = th.Amount.Value ?? 0; }
        if (th.Code.Value == "LUMP_SUM_ASSET") { lumpLimit = th.Amount.Value ?? 0; }
        if (th.Code.Value == "SME_IMMEDIATE") { smeLimit = th.Amount.Value ?? 0; }
    }

    if (smallLimit > 0 && cost < smallLimit)
    {
        MethodHint.Value = $"取得価額 {cost:#,0} 円 < {smallLimit:#,0} 円: 消耗品費等で全額損金（資産計上不要）が可能です";
    }
    else if (lumpLimit > 0 && cost < lumpLimit)
    {
        MethodHint.Value = $"取得価額 {cost:#,0} 円 < {lumpLimit:#,0} 円: 一括償却資産（3年均等）を選択できます";
    }
    else if (smeLimit > 0 && cost < smeLimit)
    {
        MethodHint.Value = $"取得価額 {cost:#,0} 円 < {smeLimit:#,0} 円: 中小企業者等の少額特例で即時償却を選択できます（年間合計300万円まで）";
    }
    else
    {
        MethodHint.Value = "通常償却（定額法または200%定率法）の対象です";
    }
}

// 対象年度の償却額を計算する（理論値ベース）
// 戻り値 0 = 対象外/償却済み
int CalcDepreciationForYear(var yearStart, var yearEnd)
{
    if (AcquisitionCost.Value == null || AcquisitionDate.Value == null) return 0;
    int cost = AcquisitionCost.Value;
    var acq = AcquisitionDate.Value;
    var method = DepreciationMethod.Value;
    if (method == "none") return 0;
    if (acq > yearEnd) return 0;

    if (method == "immediate")
    {
        // 取得年度に全額
        if (acq >= yearStart && acq <= yearEnd) return cost;
        return 0;
    }

    if (method == "lump_sum_3yr")
    {
        // 3年均等（月割なし・取得年度から3年）
        var annual3 = cost / 3;
        // 対象年度が取得年度から何年目か
        var k3 = YearIndex(acq, yearStart, yearEnd);
        if (k3 < 1 || k3 > 3) return 0;
        if (k3 == 3) { return cost - annual3 * 2; }  // 端数は最終年度で調整
        return annual3;
    }

    int life = (int)(UsefulLife.Value ?? 0);
    if (life <= 0) return 0;

    if (method == "straight_line")
    {
        int annual = cost / life;
        var k = YearIndex(acq, yearStart, yearEnd);
        if (k < 1) return 0;
        // 取得年度は月割（取得月〜年度末の月数 / 12）
        int firstYearAmount = annual * MonthsFromAcqToYearEnd(acq, yearStart) / 12;
        // 累計（対象年度の前まで）
        int accumulated = 0;
        if (k > 1)
        {
            accumulated = firstYearAmount + annual * (k - 2);
        }
        int remaining = cost - 1 - accumulated;  // 残存簿価1円
        if (remaining <= 0) return 0;
        int amount = annual;
        if (k == 1) { amount = firstYearAmount; }
        if (amount > remaining) { amount = remaining; }
        return amount;
    }

    if (method == "declining_200")
    {
        // 200%定率法（簡易: 保証率・改定償却率は未対応）。年次で簿価×(2/耐用年数)
        decimal rate = 2;
        rate = rate / life;
        var k2 = YearIndex(acq, yearStart, yearEnd);
        if (k2 < 1) return 0;
        int book = cost;
        int amount2 = 0;
        for (var i = 1; i <= k2; i++)
        {
            amount2 = book * rate;
            if (i == 1)
            {
                amount2 = amount2 * MonthsFromAcqToYearEnd(acq, yearStart) / 12;
            }
            if (amount2 > book - 1) { amount2 = book - 1; }
            if (i < k2) { book = book - amount2; }
        }
        if (amount2 <= 0) return 0;
        return amount2;
    }

    return 0;
}

// 取得日が対象年度の何年目にあたるか（取得年度=1。対象外は 0）
//
// 年度の開始月日は対象年度（yearStart）から取る。暦年の差で近似してはいけない——
// 年度開始月より前に取得した資産（4/1 開始なら 1〜3 月取得）で 1 年ずれ、
// 2 年目が 0 円・3 年目が再び「取得年度」と判定されて初年度の月割を二重計上する。
// 理論値スケジュールがそのまま仕訳金額になる（GenerateDep_OnClick）ので、
// ずれは取得価額を超える償却として現れる（残存簿価がマイナスになる）。
int YearIndex(var acq, var yearStart, var yearEnd)
{
    if (acq > yearEnd) return 0;
    if (acq >= yearStart) return 1;
    // 取得日が属する年度の開始年（年度の開始月日は yearStart と同じとみなす）
    var acqFyStartYear = acq.Year;
    if (acq.Month < yearStart.Month) { acqFyStartYear = acq.Year - 1; }
    if (acq.Month == yearStart.Month && acq.Day < yearStart.Day) { acqFyStartYear = acq.Year - 1; }
    return yearStart.Year - acqFyStartYear + 1;
}

// 取得月から年度末までの月数（取得月を 1 か月目として数える。取得年度の月割に使う）
// 年度の開始月も yearStart から取る（4 月起点を決め打ちしない）
int MonthsFromAcqToYearEnd(var acq, var yearStart)
{
    var elapsed = acq.Month - yearStart.Month;
    if (elapsed < 0) { elapsed = elapsed + 12; }
    return 12 - elapsed;
}

// 科目マスタの「固定資産科目」フラグ（ADR-0063）。未設定・見つからないときは false（安全側）
bool IsFixedAssetAccount(object accountId)
{
    if (accountId == null) return false;
    var s = new ModuleSearcher<Account>();
    s.AddEquals(a => a.Id.Value, accountId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return false;
    return ((Account)found).IsFixedAssetAccount.Value == true;
}

// ============================================================
// 除却・売却（BUG-0095・ADR-0070）
// ============================================================

// この資産の償却仕訳の合計（直接法なので、これが減価償却累計額にあたる）
int AccumulatedDepreciation()
{
    if (this.Id.Value == null) return 0;
    var total = 0;
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "depreciation");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    js.AddEquals(e => e.Status.Value, "posted");
    foreach (var row in js.Execute())
    {
        var je = (JournalEntry)row;
        foreach (var lrow in je.Lines.Rows)
        {
            var l = (JournalLine)lrow;
            if (l.Dc.Value != "D") continue;          // 借方＝減価償却費の行だけ数える
            if (l.Amount.Value == null) continue;
            total = total + l.Amount.Value;
        }
    }
    return total;
}

// 帳簿価額 = 取得価額 − 償却累計
int BookValue()
{
    var cost = AcquisitionCost.Value ?? 0;
    return cost - AccumulatedDepreciation();
}

// 処分仕訳（この資産の除却／売却で起票したもの）を 1 本返す。無ければ null
JournalEntry FindDisposalJournal()
{
    if (this.Id.Value == null) return null;
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "disposal");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    var found = js.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (JournalEntry)found;
}

// 日付が属する会計年度（この画面には TargetYear があるが、処分は「本日」で起票するので別途要る）
FiscalYear ResolveYearForDate(var d)
{
    if (d == null) return null;
    var s = new ModuleSearcher<FiscalYear>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, d);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, d);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalYear)found;
}

// 日付が属する月次期間（境界日の罠を避けるため、その月の月初日で解決する。
// 月末日は辞書順比較で外れることがある——Receipt.mod.cs:347 と同じ既知の罠）
FiscalPeriod ResolvePeriodForDate(var d)
{
    if (d == null) return null;
    var firstDay = new DateTime(d.Year, d.Month, 1);
    var s = new ModuleSearcher<FiscalPeriod>();
    s.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    s.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (FiscalPeriod)found;
}

// account_role（ddl/630）で科目を引く。**科目コードを直書きしない**ための入口
Account FindAccountByRole(string role)
{
    var s = new ModuleSearcher<Account>();
    s.AddEquals(a => a.AccountRole.Value, role);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (Account)found;
}

// 処分まわりの表示とボタンの出し分け
void UpdateDisposalUi()
{
    var disposed = (Status.Value == "retired" || Status.Value == "sold");
    var saved = !this.IsNewData;

    if (saved)
    {
        var acc = AccumulatedDepreciation();
        var cost = AcquisitionCost.Value ?? 0;
        BookValueLabel.Text = $"帳簿価額: {cost - acc:#,0} 円（取得価額 {cost:#,0} − 償却累計 {acc:#,0}）";
    }
    else
    {
        BookValueLabel.Text = "";
    }
    BookValueLabel.IsVisible = saved;

    RetireButton.IsVisible = saved && !disposed;
    SellButton.IsVisible = saved && !disposed;
    CancelDisposalButton.IsVisible = saved && disposed;
    DisposalAmountLabel.IsVisible = saved;
    DisposalAmount.IsVisible = saved;
    DisposalAmount.IsViewOnly = disposed;

    if (!saved)
    {
        DisposalHint.Text = "";
        DisposalHint.IsVisible = false;
        return;
    }
    if (disposed)
    {
        var je = FindDisposalJournal();
        var no = (je == null) ? "（仕訳なし）" : $"No.{je.JournalNo.Value}";
        var what = (Status.Value == "sold") ? "売却" : "除却";
        DisposalHint.Text = $"この資産は {RetiredDate.Value:yyyy/MM/dd} に{what}済みです（処分仕訳 {no}）。"
            + "「処分を取り消す」で仕訳ごと巻き戻せます。ただしその仕訳の期間が締め済みなら取り消せません"
            + "（締めた期の数字は動かさない・ADR-0070）。その場合は当期に反対仕訳を起こしてください。";
    }
    else
    {
        DisposalHint.Text = "「除却する」＝ 帳簿価額を固定資産除却損へ振り替えて資産を落とします（売却価額は使いません）。"
            + "　／　「売却する」＝ 売却価額を未収入金に立て、帳簿価額との差額を固定資産売却益／売却損に振り替えます。"
            + "　どちらも処分日は本日で起票します（締め済みの期間には起票できません）。";
    }
    DisposalHint.IsVisible = true;
}

void Retire_OnClick()
{
    DoDisposal(false);
}

void Sell_OnClick()
{
    DoDisposal(true);
}

// 除却・売却の共通処理。isSale=false なら除却（帳簿価額を全額除却損へ）
void DoDisposal(bool isSale)
{
    if (this.IsNewData) { Toaster.Error("資産を保存してから実行してください"); return; }
    if (Status.Value == "retired" || Status.Value == "sold") { Toaster.Error("この資産は既に処分済みです"); return; }
    if (!IsFixedAssetAccount(AssetAccount.Value))
    {
        Toaster.Error("資産計上科目が「固定資産科目」ではありません。科目マスタで固定資産科目にするか、この資産の計上科目を選び直してください");
        return;
    }

    var sale = DisposalAmount.Value ?? 0;
    if (isSale && sale <= 0) { Toaster.Error("売却価額（税抜）を入力してください"); return; }

    var book = BookValue();
    if (book < 0)
    {
        Toaster.Error($"帳簿価額がマイナス（{book:#,0} 円）です。償却仕訳を確認してから処分してください");
        return;
    }

    // 処分日は本日。締め済みの期間には起票しない（ADR-0070）。
    // フォールバックはしない——処分日を勝手にずらすと台帳の除却日と帳簿がずれるので、
    // はっきり止めて期間を開けさせる
    var dispDate = DateOnly.FromDateTime(DateTime.Today);
    var typedFy = ResolveYearForDate(dispDate);
    if (typedFy == null) { Toaster.Error("本日の日付に対応する会計年度がありません"); return; }
    var typedPeriod = ResolvePeriodForDate(dispDate);
    if (typedPeriod == null) { Toaster.Error("本日の日付に対応する月次期間がありません"); return; }
    if (typedPeriod.Status.Value == "closed")
    {
        Toaster.Error("当月の月次期間が締め済みです。期間を再オープンしてから処分してください");
        return;
    }

    var assetName = Name.Value ?? "";
    var what = isSale ? "売却" : "除却";
    var diff = sale - book;   // 売却のときだけ意味を持つ（プラス＝売却益）
    var gainLoss = (diff >= 0) ? "売却益" : "売却損";
    var detail = isSale
        ? $"売却価額 {sale:#,0} 円／帳簿価額 {book:#,0} 円／差額 {diff:#,0} 円（{gainLoss}）"
        : $"帳簿価額 {book:#,0} 円を固定資産除却損へ振り替えます";
    var answer = MessageBox.Show(
        $"固定資産「{assetName}」を{what}します。{detail}。処分日は本日（{dispDate:yyyy/MM/dd}）です。よろしいですか？",
        $"{what}する", "キャンセル");
    if (answer != $"{what}する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 役割で科目を引く（コード直書きをしない・ddl/630）
    var roleName = "disposal_loss";
    if (isSale) { roleName = (diff < 0) ? "sale_loss" : "sale_gain"; }
    var lossAcc = FindAccountByRole(roleName);
    if (lossAcc == null)
    {
        Toaster.Error("処分の振替先科目が見つかりません（科目マスタの「役割」を確認してください）");
        return;
    }
    Account recvAcc = null;
    if (isSale)
    {
        recvAcc = FindAccountByRole("disposal_receivable");
        if (recvAcc == null) { Toaster.Error("売却代金の未収科目が見つかりません（科目マスタの「役割」を確認してください）"); return; }
    }

    // 明細を組み立てる（借方・貸方・科目・金額）
    var dcList = new List<string>();
    var accList = new List<object>();
    var amtList = new List<int>();
    if (isSale)
    {
        dcList.Add("D"); accList.Add(recvAcc.Id.Value); amtList.Add(sale);          // 未収入金
        dcList.Add("C"); accList.Add(AssetAccount.Value); amtList.Add(book);        // 資産を落とす
        if (diff > 0) { dcList.Add("C"); accList.Add(lossAcc.Id.Value); amtList.Add(diff); }        // 売却益
        if (diff < 0) { dcList.Add("D"); accList.Add(lossAcc.Id.Value); amtList.Add(0 - diff); }    // 売却損
    }
    else
    {
        dcList.Add("D"); accList.Add(lossAcc.Id.Value); amtList.Add(book);          // 除却損
        dcList.Add("C"); accList.Add(AssetAccount.Value); amtList.Add(book);        // 資産を落とす
    }

    var nextNo = new JournalEntry().NextJournalNo(typedFy.Id.Value);
    var je = new JournalEntry();
    je.EntryDate.Value = dispDate;
    je.EntryType.Value = "auto";
    je.Description.Value = $"固定資産{what} {assetName}";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "disposal";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(dcList.Count);
    var idx = -1;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx + 1;
        l.Dc.Value = dcList[idx];
        l.Account.Value = accList[idx];
        l.Amount.Value = amtList[idx];
        l.InputAmount.Value = amtList[idx];
        l.TaxInputMode.Value = "none";
        l.Description.Value = $"固定資産{what} {assetName}";
        if (Department.Value != null) { l.Department.Value = Department.Value; }
    }
    // 処分は内部振替なので全明細を「対象外」にする（償却と同じ理由・ADR-0053）。
    // 売却の消費税は現状このアプリでは自動計算しない（ddl/630 の注記）
    je.MarkAllLinesOutOfScope();
    je.FillMissingDepartments();
    var ret = je.Submit();
    if (ret != true) { Toaster.Error($"{what}仕訳の生成に失敗しました"); return; }

    Status.Value = isSale ? "sold" : "retired";
    RetiredDate.Value = dispDate;
    var retSelf = this.Submit();
    if (retSelf != true)
    {
        Toaster.Error($"{what}仕訳 No.{nextNo} は生成しましたが、台帳の状態更新に失敗しました。画面を開き直してもう一度お試しください");
        return;
    }
    UpdateDisposalUi();
    Toaster.Success($"固定資産「{assetName}」を{what}しました（仕訳 No.{nextNo}・{detail}）");
}

// 処分の取消（ADR-0070: 締め前なら仕訳ごと巻き戻す／締め済みなら取り消せない）
void CancelDisposal_OnClick()
{
    if (Status.Value != "retired" && Status.Value != "sold") { Toaster.Error("この資産は処分済みではありません"); return; }

    var je = FindDisposalJournal();
    if (je != null)
    {
        var pd = ResolvePeriodForDate(je.EntryDate.Value);
        if (pd == null) { Toaster.Error("処分仕訳の日付に対応する月次期間がありません"); return; }
        if (pd.Status.Value == "closed")
        {
            Toaster.Error($"処分仕訳 No.{je.JournalNo.Value} の期間は締め済みのため取り消せません。"
                + "当期に反対仕訳（赤伝）を起票して打ち消してください（締めた期の数字は動かさない・ADR-0070）");
            return;
        }
    }

    var confirmText = (je == null)
        ? "処分の記録を取り消して「使用中」に戻します。よろしいですか？"
        : $"処分仕訳 No.{je.JournalNo.Value} を削除して「使用中」に戻します。よろしいですか？";
    var answer = MessageBox.Show(confirmText, "取り消す", "キャンセル");
    if (answer != "取り消す") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var no = "";
    if (je != null)
    {
        no = $"{je.JournalNo.Value}";
        var okDel = je.Delete();
        if (okDel != true) { Toaster.Error("処分仕訳の削除に失敗しました"); return; }
    }

    Status.Value = "in_use";
    RetiredDate.Value = null;
    DisposalAmount.Value = null;
    var retSelf = this.Submit();
    if (retSelf != true) { Toaster.Error("台帳の状態更新に失敗しました。画面を開き直してもう一度お試しください"); return; }
    UpdateDisposalUi();
    var doneMsg = (no == "") ? "処分を取り消しました" : $"処分仕訳 No.{no} を削除し、処分を取り消しました";
    Toaster.Success(doneMsg);
}

void GenerateDep_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("資産を保存してから実行してください");
        return;
    }
    if (TargetYear.Value == null)
    {
        Toaster.Error("対象年度を選択してください");
        return;
    }

    // 資産計上科目の関門（ADR-0063）。この科目は償却仕訳の**貸方**になるので、
    // 固定資産科目でないものが入っていると「借 減価償却費 / 貸 現金」のような仕訳を作ってしまう。
    // 候補の絞り込み（AssetAccount の SearchCondition）は選ばせない工夫であって関門ではない——
    // 旧データや、資産が参照したままフラグを外されたマスタはここでしか止められない。
    // 黙って壊すより、はっきり止めて直させる（静かな失敗を作らない）
    if (!IsFixedAssetAccount(AssetAccount.Value))
    {
        Toaster.Error("資産計上科目が「固定資産科目」ではありません。"
            + "科目マスタで固定資産科目にするか、この資産の計上科目を選び直してください");
        return;
    }

    // 償却仕訳を取り消す導線がこの画面に無い＝不可逆なので確認する（ADR-0062）
    var answer = MessageBox.Show(
        "この年度の減価償却仕訳を生成します。生成した仕訳を取り消す導線はこの画面にありません"
        + "（誤りは振替伝票側で訂正してください）。よろしいですか？",
        "生成する", "キャンセル");
    if (answer != "生成する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddEquals(e => e.Id.Value, TargetYear.Value);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null)
    {
        Toaster.Error("対象年度が見つかりません");
        return;
    }
    var typedFy = (FiscalYear)fy;
    if (typedFy.Status.Value == "closed")
    {
        Toaster.Error("対象年度は締め済みです");
        return;
    }

    // 締め済み月次期間ガード（仕訳日＝期末日が属する期間。月初日で解決＝境界日の罠回避）
    var eod = typedFy.EndDate.Value;
    var periodFirstDay = new DateTime(eod.Year, eod.Month, 1);
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, periodFirstDay);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, periodFirstDay);
    var period = ps.ExecuteFirstOrDefault();
    if (period != null && ((FiscalPeriod)period).Status.Value == "closed")
    {
        Toaster.Error("期末月の期間は締め済みです。期間を再オープンしてから生成してください");
        return;
    }

    // 二重生成ガード
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "depreciation");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    js.AddEquals(e => e.FiscalYearRef.Value, TargetYear.Value);
    if (js.Execute().Count > 0)
    {
        Toaster.Error("この年度の償却仕訳は既に生成済みです");
        return;
    }

    var amount = CalcDepreciationForYear(typedFy.StartDate.Value, typedFy.EndDate.Value);
    if (amount <= 0)
    {
        Toaster.Info("この年度の償却額はありません（対象外または償却済み）");
        return;
    }

    // 減価償却費(6300)の科目を取得
    var accS = new ModuleSearcher<Account>();
    accS.AddEquals(e => e.Code.Value, "6300");
    var depAcc = accS.ExecuteFirstOrDefault();
    if (depAcc == null)
    {
        Toaster.Error("減価償却費(6300)の科目がありません");
        return;
    }
    var typedDepAcc = (Account)depAcc;

    // 伝票番号の採番（正典: JournalEntry.NextJournalNo。BUG-0069 で一本化）
    var nextNo = new JournalEntry().NextJournalNo(TargetYear.Value);

    // 償却仕訳を生成（借方 減価償却費 / 貸方 資産科目 = 直接法）
    var je = new JournalEntry();
    je.EntryDate.Value = typedFy.EndDate.Value;
    je.EntryType.Value = "auto";
    je.Description.Value = $"減価償却 {Name.Value}（{typedFy.Name.Value}）";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = TargetYear.Value;
    je.SourceType.Value = "depreciation";
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(2);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        l.TaxInputMode.Value = "none";
        l.Description.Value = $"減価償却 {Name.Value}";
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = typedDepAcc.Id.Value;
            if (Department.Value != null) { l.Department.Value = Department.Value; }
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = AssetAccount.Value;
        }
    }
    // 減価償却は内部振替なので全明細を「対象外」に上書きする（ADR-0053）。
    // 科目の既定に任せると、貸方の固定資産科目に取得時の「課税仕入 10%」が入り消費税集計表が狂う。
    je.MarkAllLinesOutOfScope();
    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    var ret = je.Submit();
    if (ret == false)
    {
        Toaster.Error("償却仕訳の生成に失敗しました");
        return;
    }
    Toaster.Success($"償却仕訳 No.{nextNo}（{amount:#,0} 円）を生成しました");
}
