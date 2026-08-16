// FixedAsset.mod.cs — 固定資産台帳
// 責務: 取得価額×取得日から少額判定（system_thresholds を期間解決）して処理方法を提案 /
//        対象年度の減価償却仕訳（entry_type=auto, source_type='depreciation'）を自動生成
// 設計: docs/04 §7。簿価は理論値ベースの年次償却（月割は取得年度のみ）。残存簿価1円。

void Detail_OnAfterInit()
{
    if (this.IsNewData)
    {
        Status.Value = "in_use";
        DepreciationMethod.Value = "straight_line";
    }
    UpdateMethodHint();
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
        int firstYearAmount = annual * MonthsFromAcqToYearEnd(acq) / 12;
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
                amount2 = amount2 * MonthsFromAcqToYearEnd(acq) / 12;
            }
            if (amount2 > book - 1) { amount2 = book - 1; }
            if (i < k2) { book = book - amount2; }
        }
        if (amount2 <= 0) return 0;
        return amount2;
    }

    return 0;
}

// 取得日が対象年度の何年目にあたるか（取得年度=1。対象年度開始日基準の近似）
int YearIndex(var acq, var yearStart, var yearEnd)
{
    if (acq >= yearStart && acq <= yearEnd) return 1;
    if (acq > yearEnd) return 0;
    // 取得年度の期首を推定するのは複雑なため、開始日の年差で近似（3月決算の年次運用で成立）
    var years = yearStart.Year - acq.Year;
    if (acq.Month >= yearStart.Month) { years = years; } else { years = years - 1; }
    return years + 1;
}

int MonthsFromAcqToYearEnd(var acq)
{
    // 取得月から年度末までの月数（3月決算前提の近似: 4月起点）
    var m = acq.Month;
    if (m >= 4) { return 12 - (m - 4); }
    return 4 - m;
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

    // 伝票番号の採番
    var ns = new ModuleSearcher<JournalEntry>();
    ns.AddEquals(e => e.FiscalYearRef.Value, TargetYear.Value);
    ns.OrderByDescending(e => e.JournalNo.Value);
    ns.Limit(1);
    var last = ns.ExecuteFirstOrDefault();
    var nextNo = 1;
    if (last != null)
    {
        var typedLast = (JournalEntry)last;
        if (typedLast.JournalNo.Value != null) { nextNo = (int)typedLast.JournalNo.Value + 1; }
    }

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
