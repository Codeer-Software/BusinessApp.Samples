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
//
// **明細は JournalLine を直接検索して数える。** 検索で取った JournalEntry の `Lines.Rows` は
// 遅延ロードで空のことがあり（実測 2026-08-18: 償却仕訳があるのに累計 0 と出た）、
// そのまま使うと**帳簿価額が取得価額のまま＝除却損が過大**になる。
// 償却累計 = **資産科目に立った「貸方 − 借方」**（直接法なので償却は資産科目の貸方に立つ）。
//
// 数える伝票は 2 種類（BUG-0340・ADR-0073）:
//   ① この資産の償却として自動生成した伝票（source_type='depreciation'）
//   ② 伝票ヘッダの「固定資産」欄でこの資産を指した伝票 —— **手で打った訂正の振替伝票**がこれ。
//      償却生成の確認ダイアログが「誤りは振替伝票側で訂正してください」と案内している以上、
//      その訂正が簿価に効かないと案内が嘘になる（旧実装は①しか見ておらず、訂正が無視されていた）
// 処分伝票（source_type='disposal'）は簿価を落とす仕訳であって償却ではないので数えない。
//
// 「借方＝減価償却費の行を足す」ではなく資産科目の増減で測るのは、訂正伝票が
// 「借方 資産科目 / 貸方 減価償却費」（＝償却の戻し）の向きにもなるため。
// 資産科目の純増減で見れば、生成分も訂正分も同じ式で正しく数えられる。
int AccumulatedDepreciation()
{
    return AccumulatedDepreciationAsOf(DateTime.Today);
}

// 基準日の属する**年度末まで**の償却だけを数える（BUG-0357）。
//
// 翌年度の償却仕訳は年度さえ作ってあれば先に起票できるので、**未到来の年度の償却が
// 帳簿価額を先食いする**。実データでも 2028-03-31 付（第19期）の償却が既に立っていて、
// 2026 年時点の簿価が 115,000 円過小に見えていた。この状態で処分すると、
// 除却損・売却損益がその分ずれる。
//
// 切り口を「基準日そのもの」ではなく「**基準日の属する年度の期末**」にするのは、
// このアプリが償却を**年度末の日付で 1 本**起票するからである。基準日で切ると、
// 期中に見たときに当年度の償却まで落ちてしまい、台帳の簿価が帳簿と食い違う。
int AccumulatedDepreciationAsOf(var asOf)
{
    if (this.Id.Value == null) return 0;
    if (AssetAccount.Value == null) return 0;
    var cutoff = asOf;
    var fyOf = ResolveYearForDate(asOf);
    if (fyOf != null && fyOf.EndDate.Value != null) { cutoff = fyOf.EndDate.Value; }

    var entryIds = new List<string>();
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "depreciation");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    js.AddEquals(e => e.Status.Value, "posted");
    foreach (var row in js.Execute())
    {
        var jd = (JournalEntry)row;
        if (cutoff != null && jd.EntryDate.Value != null && jd.EntryDate.Value > cutoff) continue;
        entryIds.Add($"{jd.Id.Value}");
    }

    var ts = new ModuleSearcher<JournalEntry>();
    ts.AddEquals(e => e.FixedAssetRef.Value, this.Id.Value);
    ts.AddEquals(e => e.Status.Value, "posted");
    foreach (var row in ts.Execute())
    {
        var je = (JournalEntry)row;
        if (je.SourceType.Value == "disposal") continue;   // 処分は償却ではない
        if (cutoff != null && je.EntryDate.Value != null && je.EntryDate.Value > cutoff) continue;
        var key = $"{je.Id.Value}";
        var known = false;
        foreach (var k in entryIds) { if (k == key) { known = true; break; } }
        if (!known) { entryIds.Add(key); }
    }

    var total = 0;
    foreach (var id in entryIds)
    {
        var ls = new ModuleSearcher<JournalLine>();
        ls.AddEquals(l => l.JournalEntryId.Value, id);
        foreach (var lrow in ls.Execute())
        {
            var l = (JournalLine)lrow;
            if (l.Amount.Value == null) continue;
            if ($"{l.Account.Value}" != $"{AssetAccount.Value}") continue;   // 資産科目の行だけ見る
            if (l.Dc.Value == "C") { total = total + l.Amount.Value; }
            else { total = total - l.Amount.Value; }
        }
    }
    if (total < 0) return 0;
    return total;
}

// 帳簿価額 = 取得価額 − 償却累計（今日時点）
int BookValue()
{
    return BookValueAsOf(DateTime.Today);
}

// 基準日時点の帳簿価額。処分はこちらを使う（処分日より後に立っている償却を数えないため）
int BookValueAsOf(var asOf)
{
    var cost = AcquisitionCost.Value ?? 0;
    return cost - AccumulatedDepreciationAsOf(asOf);
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
// この資産の償却仕訳（自動生成・手入力の訂正とも）が 1 本でもあるか
bool HasDepreciationJournal()
{
    if (this.Id.Value == null) return false;
    var js = new ModuleSearcher<JournalEntry>();
    js.AddEquals(e => e.SourceType.Value, "depreciation");
    js.AddEquals(e => e.SourceId.Value, this.Id.Value);
    if (js.Execute().Count > 0) return true;
    var ts = new ModuleSearcher<JournalEntry>();
    ts.AddEquals(e => e.FixedAssetRef.Value, this.Id.Value);
    foreach (var row in ts.Execute())
    {
        if (((JournalEntry)row).SourceType.Value != "disposal") return true;
    }
    return false;
}

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

// 売却の税区分を解決する。画面で選ばれていなければ**売上の既定税区分**（tax_categories.default_for='sales'）を使う。
// 土地のように非課税の売却は画面で「非課税売上」を選ぶ（BUG-0338・開発者判断 2026-08-18）
TaxCategory ResolveSaleTaxCategory()
{
    if (DisposalTaxCategory.Value != null)
    {
        var s0 = new ModuleSearcher<TaxCategory>();
        s0.AddEquals(c => c.Id.Value, DisposalTaxCategory.Value);
        var f0 = s0.ExecuteFirstOrDefault();
        if (f0 != null) return (TaxCategory)f0;
    }
    var s = new ModuleSearcher<TaxCategory>();
    s.AddEquals(c => c.DefaultFor.Value, "sales");
    s.AddEquals(c => c.IsActive.Value, true);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (TaxCategory)found;
}

// 税区分に紐づく税率(%)。解決できなければ 0（＝税額を立てない）
decimal SaleTaxRatePercent(TaxCategory tcat)
{
    if (tcat == null) return 0;
    if (tcat.Rate.Value == null) return 0;
    var rs = new ModuleSearcher<TaxRate>();
    rs.AddEquals(r => r.Id.Value, tcat.Rate.Value);
    var foundRate = rs.ExecuteFirstOrDefault();
    if (foundRate == null) return 0;
    return ((TaxRate)foundRate).RatePercent.Value ?? 0;
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
        var bvWord = (Status.Value == "retired" || Status.Value == "sold") ? "処分時点の帳簿価額" : "帳簿価額";
        BookValueLabel.Text = $"{bvWord}: {cost - acc:#,0} 円（取得価額 {cost:#,0} − 償却累計 {acc:#,0}）";

        // **償却仕訳が 1 本でもある資産は、資産計上科目を変えさせない**（BUG-0358）。
        // 償却累計は「その資産科目に立った貸方−借方」で数えるので（ADR-0073）、
        // あとから科目を選び直すと過去の償却が集計から外れ、**償却累計が黙って 0 になる**。
        // 帳簿価額が取得価額のまま表示され、除却損・売却損益が過大になる。
        // 科目を直したいときは、償却仕訳を取り消してから変える
        AssetAccount.IsViewOnly = HasDepreciationJournal();
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
        // ラベルは Markdown を解釈しない（`**` がそのまま出る）。強調記号は使わず「」で括る
        var defCatName = "（売上の既定税区分）";
        var defCat = ResolveSaleTaxCategory();
        if (defCat != null) { defCatName = defCat.Name.Value ?? defCatName; }
        DisposalHint.Text = "「除却する」＝ 帳簿価額を固定資産除却損へ振り替えて資産を落とします（売却価額・税区分は使いません）。"
            + "　／　「売却する」＝ 総額法で起票します（借方 未収入金〈税込〉・固定資産売却原価〈簿価〉／"
            + "貸方 固定資産〈簿価〉・固定資産売却益〈対価〉・仮受消費税）。差引の純額は売却損益と同じですが、"
            + "消費税の課税標準が対価と一致するのはこの形だけです。"
            + $"　税区分を選ばないときは「{defCatName}」が使われます。土地のように非課税の売却は「非課税売上」を選んでください。"
            + "　処分日が期の途中なら、その年度に使った月数ぶんの減価償却を処分日付で先に 1 本起票します。"
            + "　どちらも処分日は本日で起票します（締め済みの期間には起票できません）。";
    }
    DisposalHint.IsVisible = true;
}

// 期首から処分月までの月数（処分月を含める。取得年度の月割が「取得月を 1 か月目として数える」ので、
// 出口側も同じ数え方にそろえる）
int MonthsFromYearStartToDisposal(var yearStart, var disposal)
{
    var elapsed = disposal.Month - yearStart.Month;
    if (elapsed < 0) { elapsed = elapsed + 12; }
    return elapsed + 1;
}

// 処分日までの期中償却（BUG-0339）。
//
// 償却仕訳は期末日付でしか作れないので、期中に処分すると**当期分の減価償却費が 0 のまま
// 全額が除却損**になり、PL の科目配分が狂う。処分の直前に、その年度に実際に使った月数ぶんを
// **処分日と同じ日付**で 1 本起票する（不変条件 E03 の備考が定める作法）。
//
// 計算だけ先に行い（Calc）、**確認ダイアログを通ってから起票する**（Post）。
// 先に起票すると「キャンセル」で償却仕訳だけが孤児として残る。
int CalcPartialYearDepreciation(FiscalYear fy, var dispDate)
{
    if (fy == null) return 0;
    if (this.Id.Value == null) return 0;

    // 当期の償却が既に立っていれば触らない（確定済みのものだけを見る）
    var exist = new ModuleSearcher<JournalEntry>();
    exist.AddEquals(e => e.SourceType.Value, "depreciation");
    exist.AddEquals(e => e.SourceId.Value, this.Id.Value);
    exist.AddEquals(e => e.FiscalYearRef.Value, fy.Id.Value);
    exist.AddEquals(e => e.Status.Value, "posted");
    if (exist.Execute().Count > 0) return 0;

    var full = CalcDepreciationForYear(fy.StartDate.Value, fy.EndDate.Value);
    if (full <= 0) return 0;

    var method = DepreciationMethod.Value;
    var amount = full;
    // **即時償却と一括償却(3年均等)は月割しない。** 即時償却は取得年度に全額が正で、
    // 一括償却も制度上は月割しない（`CalcDepreciationForYear` も月割していない）
    if (method != "immediate" && method != "lump_sum_3yr")
    {
        // その年度に実際に使った月数 ÷ その年度の本来の月数。
        // **取得年度は `full` が既に取得月按分済み**なので、分母も取得月起点にしないと二重に按分される
        var acq = AcquisitionDate.Value;
        var startMonthBase = fy.StartDate.Value;
        var denom = 12;
        if (acq != null && acq >= fy.StartDate.Value && acq <= fy.EndDate.Value)
        {
            startMonthBase = acq;
            denom = MonthsFromAcqToYearEnd(acq, fy.StartDate.Value);
        }
        if (denom <= 0) { denom = 12; }
        var used = MonthsFromYearStartToDisposal(startMonthBase, dispDate);
        if (used <= 0) return 0;
        if (used > denom) { used = denom; }
        amount = full * used / denom;
    }
    if (amount <= 0) return 0;

    // 残存簿価 1 円を割らない（理論値の丸めで割り込むことがある）
    var bookNow = BookValue();
    if (amount > bookNow - 1) { amount = bookNow - 1; }
    if (amount <= 0) return 0;
    return amount;
}

// 期中償却を起票する。**摘要に目印を入れる**——処分の取消でこの 1 本だけを戻すために、
// 通常の期末償却と機械的に区別できる必要がある（日付だけで判別すると、年度末に処分したとき
// 期末償却まで巻き添えで消える）
bool PostPartialDepreciationJournal(FiscalYear fy, var dispDate, int amount)
{
    if (amount <= 0) return true;
    var depAcc = FindAccountByRole("depreciation_expense");
    if (depAcc == null)
    {
        var accS = new ModuleSearcher<Account>();
        accS.AddEquals(e => e.Code.Value, "6300");
        var f = accS.ExecuteFirstOrDefault();
        if (f == null) { Toaster.Error("減価償却費の科目がありません"); return false; }
        depAcc = (Account)f;
    }

    var nextNo = new JournalEntry().NextJournalNo(fy.Id.Value);
    var je = new JournalEntry();
    je.EntryDate.Value = dispDate;
    je.EntryType.Value = "auto";
    je.Description.Value = $"減価償却 {Name.Value}（{PartialDepMark()}）";
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = fy.Id.Value;
    je.SourceType.Value = "depreciation";
    je.FixedAssetRef.Value = this.Id.Value;
    je.SourceId.Value = this.Id.Value;
    je.Lines.AddRows(2);
    var i2 = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        i2 = i2 + 1;
        l.LineNo.Value = i2;
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        l.TaxInputMode.Value = "none";
        l.Description.Value = $"減価償却 {Name.Value}（期中）";
        if (i2 == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = depAcc.Id.Value;
            if (Department.Value != null) { l.Department.Value = Department.Value; }
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = AssetAccount.Value;
        }
    }
    je.MarkAllLinesOutOfScope();
    je.FillMissingDepartments();
    var ret = je.Submit();
    if (ret != true) { Toaster.Error("処分までの期中償却の生成に失敗しました"); return false; }
    return true;
}

// 期中償却の目印（摘要に入れる文字列）。取消のときこの文字列で自分の起票分を見分ける
string PartialDepMark()
{
    return "処分までの期中償却";
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
    // 処分仕訳はあるのに状態が使用中のまま＝「仕訳の保存は成功したが直後の状態更新が失敗した」中断状態。
    // ここで作り直すと**2 本目の処分仕訳**が立って資産が二重に落ちる。作らずに状態だけ進めて自己修復する
    // （経費の仕訳生成と同じ作法・BUG-0311）
    var already = FindDisposalJournal();
    if (already != null)
    {
        var wasSale = (DisposalAmount.Value ?? 0) > 0;
        Status.Value = wasSale ? "sold" : "retired";
        if (RetiredDate.Value == null) { RetiredDate.Value = already.EntryDate.Value; }
        var retFix = this.Submit();
        if (retFix != true)
        {
            Toaster.Error($"処分仕訳 No.{already.JournalNo.Value} は既に生成済みですが、台帳の状態更新に失敗しました。画面を開き直してもう一度お試しください");
            return;
        }
        UpdateDisposalUi();
        Toaster.Info($"処分仕訳 No.{already.JournalNo.Value} は既に生成されていました。二重には作らず、台帳の状態だけ合わせました");
        return;
    }
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
    // 簿価 0（即時償却・一括償却で償却しきった資産）を**除却**するときは、振り替える金額が無い。
    // 0 円の仕訳行を作ると帳簿にノイズが残るだけなので、仕訳は起こさず台帳の状態だけ変える
    if (!isSale && book == 0)
    {
        var ans0 = MessageBox.Show(
            $"固定資産「{Name.Value}」は帳簿価額が 0 円です（償却済み）。振り替える金額が無いので**仕訳は作らず**、台帳の状態だけ除却にします。よろしいですか？",
            "除却する", "キャンセル");
        if (ans0 != "除却する") return;
        using var suspend0 = this.SuspendNotifyStateChanged();
        Status.Value = "retired";
        RetiredDate.Value = DateOnly.FromDateTime(DateTime.Today);
        var ret0 = this.Submit();
        if (ret0 != true) { Toaster.Error("台帳の状態更新に失敗しました"); return; }
        UpdateDisposalUi();
        Toaster.Success($"固定資産「{Name.Value}」を除却しました（帳簿価額 0 円のため仕訳は作っていません）");
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

    // 期中償却は**金額だけ先に出す**（起票は確認ダイアログのあと）。簿価はその分だけ下がる見込みになる
    var partial = CalcPartialYearDepreciation(typedFy, dispDate);
    if (partial > 0) { book = book - partial; }

    var assetName = Name.Value ?? "";
    var what = isSale ? "売却" : "除却";
    var diff = sale - book;   // 売却のときだけ意味を持つ（プラス＝売却益）
    var gainLoss = (diff >= 0) ? "売却益" : "売却損";
    var diffAbs = (diff >= 0) ? diff : (0 - diff);
    var partialNote = (partial > 0) ? $"（処分までの期中償却 {partial:#,0} 円を先に計上します）" : "";
    var detail = isSale
        ? $"売却価額 {sale:#,0} 円（税抜）／帳簿価額 {book:#,0} 円／差額 {diffAbs:#,0} 円（{gainLoss}）{partialNote}"
        : $"帳簿価額 {book:#,0} 円を固定資産除却損へ振り替えます{partialNote}";
    var answer = MessageBox.Show(
        $"固定資産「{assetName}」を{what}します。{detail}。処分日は本日（{dispDate:yyyy/MM/dd}）です。よろしいですか？",
        $"{what}する", "キャンセル");
    if (answer != $"{what}する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 期中償却をここで起票する（確認を通ったあと。先に起こすとキャンセルで孤児が残る）
    if (!PostPartialDepreciationJournal(typedFy, dispDate, partial)) return;

    // 役割で科目を引く（コード直書きをしない・ddl/630, 710）。
    // 総額法なので、売却では**損益の符号で科目を変えない**（対価は必ず売却益・簿価は必ず売却原価）
    Account lossAcc = null;
    Account gainAcc = null;
    Account recvAcc = null;
    if (isSale)
    {
        gainAcc = FindAccountByRole("sale_gain");
        if (gainAcc == null) { Toaster.Error("固定資産売却益の科目が見つかりません（科目マスタの「役割」に sale_gain を設定してください）"); return; }
        recvAcc = FindAccountByRole("disposal_receivable");
        if (recvAcc == null) { Toaster.Error("売却代金の未収科目が見つかりません（科目マスタの「役割」に disposal_receivable を設定してください）"); return; }
    }
    else
    {
        lossAcc = FindAccountByRole("disposal_loss");
        if (lossAcc == null) { Toaster.Error("固定資産除却損の科目が見つかりません（科目マスタの「役割」に disposal_loss を設定してください）"); return; }
    }

    // 売却の消費税（BUG-0338）。**対価（売却価額・税抜）に対して**計算する。切り捨て（ADR-0050 と同じ流儀）。
    // 税区分が課税売上でなければ税率 0 になり、従来どおり税額は立たない（土地の売却など）
    var saleTaxCat = isSale ? ResolveSaleTaxCategory() : null;
    if (isSale && saleTaxCat == null)
    {
        // 静かに税ゼロへ落ちると BUG-0338 が再発したことに誰も気づけない
        Toaster.Error("売却の税区分が決まりません。画面で税区分を選ぶか、税区分マスタで「既定用途＝売上」を設定してください");
        return;
    }
    var saleTax = 0;
    object saleTaxCatId = null;
    object taxAccId = null;
    if (isSale && saleTaxCat != null)
    {
        saleTaxCatId = saleTaxCat.Id.Value;
        decimal pct = SaleTaxRatePercent(saleTaxCat);
        if (pct > 0)
        {
            saleTax = (int)(sale * pct / 100);     // 切り捨て（家の作法・ADR-0050）
            var ta = FindAccountByRole("consumption_tax_payable");
            if (ta == null)
            {
                var tas = new ModuleSearcher<Account>();
                tas.AddEquals(e => e.Code.Value, "2200");
                var taf = tas.ExecuteFirstOrDefault();
                if (taf != null) { ta = (Account)taf; }
            }
            if (ta == null)
            {
                Toaster.Error("仮受消費税の科目が見つかりません（科目マスタの「役割」に consumption_tax_payable を設定してください）");
                return;
            }
            taxAccId = ta.Id.Value;
        }
    }
    if (saleTax <= 0) { taxAccId = null; }

    // 対象外の税区分を引く。**MarkRemainingLinesOutOfScope では埋まらない**——
    // 科目の既定（固定資産なら取得時の「課税仕入 10%」）が既に入っているので「残り」に該当せず、
    // 資産の貸方行が課税仕入のまま posted されて消費税集計表が膨らむ（ADR-0053 の事故の再発。実測で確認）
    object outOfScopeId = null;
    var oss = new ModuleSearcher<TaxCategory>();
    oss.AddEquals(c => c.Code.Value, "OUT_OF_SCOPE");
    var osf = oss.ExecuteFirstOrDefault();
    if (osf == null) { Toaster.Error("税区分「対象外」がありません（税区分マスタを確認してください）"); return; }
    outOfScopeId = ((TaxCategory)osf).Id.Value;

    // 明細を組み立てる（借方・貸方・科目・金額・税区分）。
    // **売却は総額法**（ddl/710）: 対価を売却益に、簿価を売却原価に、それぞれ総額で立てる。
    // 差額方式だと消費税の課税標準が「売却損益の額」になり、売却損のときは
    // **損失が課税売上として ＋計上**されてしまう（消費税集計表は dc=dc_normal を +1 と数えるため）。
    // 総額法なら課税標準＝対価で一致し、貸借も損益の符号に関わらず必ず釣り合う（分岐が消える）。
    var dcList = new List<string>();
    var accList = new List<object>();
    var amtList = new List<int>();
    var catList = new List<object>();
    var taxFlag = new List<bool>();
    var taxParentNo = 1;
    if (isSale)
    {
        var costAcc = FindAccountByRole("sale_cost");
        if (costAcc == null) { Toaster.Error("固定資産売却原価の科目が見つかりません（科目マスタの「役割」に sale_cost を設定してください）"); return; }
        dcList.Add("D"); accList.Add(recvAcc.Id.Value);   amtList.Add(sale + saleTax); catList.Add(outOfScopeId); taxFlag.Add(false);  // 未収入金（税込）
        if (book > 0)
        {
            dcList.Add("D"); accList.Add(costAcc.Id.Value);  amtList.Add(book); catList.Add(outOfScopeId); taxFlag.Add(false);        // 売却原価（簿価）
            dcList.Add("C"); accList.Add(AssetAccount.Value); amtList.Add(book); catList.Add(outOfScopeId); taxFlag.Add(false);       // 資産を落とす
        }
        dcList.Add("C"); accList.Add(gainAcc.Id.Value);   amtList.Add(sale); catList.Add(saleTaxCatId); taxFlag.Add(false);           // 売却益（対価・課税標準）
        taxParentNo = dcList.Count;   // 税行の親は必ず「売却益（対価）」の行
        if (saleTax > 0) { dcList.Add("C"); accList.Add(taxAccId); amtList.Add(saleTax); catList.Add(saleTaxCatId); taxFlag.Add(true); }
    }
    else
    {
        dcList.Add("D"); accList.Add(lossAcc.Id.Value);   amtList.Add(book); catList.Add(outOfScopeId); taxFlag.Add(false);           // 除却損
        dcList.Add("C"); accList.Add(AssetAccount.Value); amtList.Add(book); catList.Add(outOfScopeId); taxFlag.Add(false);           // 資産を落とす
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
    je.FixedAssetRef.Value = this.Id.Value;
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
        if (catList[idx] != null) { l.TaxCategory.Value = catList[idx]; }
        if (taxFlag[idx])
        {
            l.IsTaxLine.Value = true;
            l.ParentLineNo.Value = taxParentNo;
            l.Description.Value = $"消費税（行{taxParentNo}）";
        }
    }
    // 税区分を明示していない行は「対象外」で埋める（処分は内部振替なので、
    // 科目の既定＝取得時の「課税仕入 10%」が入ると消費税集計表が狂う・ADR-0053）。
    // **MarkAllLinesOutOfScope は使えない**——売却では課税売上の行と税行を残す必要がある
    je.MarkRemainingLinesOutOfScope();
    je.FillMissingDepartments();
    var ret = je.Submit();
    if (ret != true) { Toaster.Error($"{what}仕訳の生成に失敗しました"); return; }

    // 台帳の確定は**DB から取り直したインスタンス**へ書く。
    // 画面のインスタンスに書いて Submit すると、「取消 → 再売却」のように
    // 同じ画面で 2 回操作したときに**処分日と売却価額が保存されない**（実測 2026-08-18）。
    // 保存済みの値を DB 基準で置き直すのが確実（#60 と同じ「DB から取り直す」作法）
    Status.Value = isSale ? "sold" : "retired";
    RetiredDate.Value = dispDate;
    if (!isSale) { DisposalAmount.Value = null; DisposalTaxCategory.Value = null; }   // 除却は売却の欄を使わない
    var retSelf = this.Submit();
    if (retSelf != true)
    {
        Toaster.Error($"{what}仕訳 No.{nextNo} は生成しましたが、台帳の状態更新に失敗しました。画面を開き直してもう一度お試しください");
        return;
    }
    // 念のため DB 側も突き合わせて、抜けていたら埋め直す
    var vs = new ModuleSearcher<FixedAsset>();
    vs.AddEquals(e => e.Id.Value, this.Id.Value);
    var vfound = vs.ExecuteFirstOrDefault();
    if (vfound != null)
    {
        var saved = (FixedAsset)vfound;
        var needFix = (saved.RetiredDate.Value == null)
            || (isSale && (saved.DisposalAmount.Value ?? 0) != sale)
            || (saved.Status.Value != (isSale ? "sold" : "retired"));
        if (needFix)
        {
            saved.Status.Value = isSale ? "sold" : "retired";
            saved.RetiredDate.Value = dispDate;
            if (isSale)
            {
                saved.DisposalAmount.Value = sale;
                if (saleTaxCatId != null) { saved.DisposalTaxCategory.Value = saleTaxCatId; }
            }
            else
            {
                saved.DisposalAmount.Value = null;
                saved.DisposalTaxCategory.Value = null;
            }
            saved.Submit();
        }
    }
    // **別インスタンス（saved）で Submit すると、画面のインスタンスの値が落ちることがある**（BUG-0375）。
    // 売却の直後だけ「除却・売却日」が空欄になり、案内文も「この資産は  に売却済みです」と
    // 日付が抜けて表示されていた（データは正しく、再読込すると直る＝表示だけの問題）。
    // DB は既に正しいので、**画面の値を入れ直してから**表示を組み直す
    Status.Value = isSale ? "sold" : "retired";
    RetiredDate.Value = dispDate;
    if (isSale)
    {
        DisposalAmount.Value = sale;
        if (saleTaxCatId != null) { DisposalTaxCategory.Value = saleTaxCatId; }
    }
    UpdateDisposalUi();
    var partialText = (partial > 0) ? $"／処分までの期中償却 {partial:#,0} 円も起票" : "";
    Toaster.Success($"固定資産「{assetName}」を{what}しました（仕訳 No.{nextNo}・{detail}{partialText}）");
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

    // 処分と同じ日付で起票した**期中償却**も一緒に戻す（BUG-0339）。
    // 残すと「使用中なのに期中で償却が切れている」状態になり、次に処分するとき二重に償却してしまう。
    // 判別は「source_type='depreciation' かつ仕訳日＝処分日」——期末日付の通常の償却とはぶつからない
    var removedPartial = 0;
    if (RetiredDate.Value != null)
    {
        var ds = new ModuleSearcher<JournalEntry>();
        ds.AddEquals(e => e.SourceType.Value, "depreciation");
        ds.AddEquals(e => e.SourceId.Value, this.Id.Value);
        ds.AddEquals(e => e.EntryDate.Value, RetiredDate.Value);
        foreach (var drow in ds.Execute())
        {
            var dje = (JournalEntry)drow;
            // **摘要の目印で自分の起票分だけを消す。** 日付だけで判別すると、年度末に処分したとき
            // 通常の期末償却まで巻き添えで消える（同じ日付になるため）
            var desc = dje.Description.Value ?? "";
            if (!desc.Contains(PartialDepMark())) continue;
            if (dje.Delete() == true) { removedPartial = removedPartial + 1; }
        }
    }

    Status.Value = "in_use";
    RetiredDate.Value = null;
    DisposalAmount.Value = null;
    var retSelf = this.Submit();
    if (retSelf != true) { Toaster.Error("台帳の状態更新に失敗しました。画面を開き直してもう一度お試しください"); return; }
    UpdateDisposalUi();
    var partialMsg = (removedPartial > 0) ? "（処分までの期中償却も戻しました）" : "";
    var doneMsg = (no == "") ? $"処分を取り消しました{partialMsg}" : $"処分仕訳 No.{no} を削除し、処分を取り消しました{partialMsg}";
    Toaster.Success(doneMsg);
}

void GenerateDep_OnClick()
{
    if (this.IsNewData)
    {
        Toaster.Error("資産を保存してから実行してください");
        return;
    }
    // 処分済みの資産に償却を足さない。資産は処分仕訳で既に貸方に落ちているので、
    // ここで償却を足すと**二重に落ちて簿価がマイナス**になる（不変条件 E03 の前提でもある）
    if (Status.Value == "retired" || Status.Value == "sold")
    {
        Toaster.Error("この資産は処分済みです。償却仕訳は生成できません（必要なら「処分を取り消す」で戻してから実行してください）");
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

    // 減価償却費の科目は**役割で引く**（ddl/630 の account_role='depreciation_expense'）。
    // コード直値だと科目体系を組み替えた瞬間に静かに壊れる。役割が未設定の環境では従来のコードで拾う
    var typedDepAcc = FindAccountByRole("depreciation_expense");
    if (typedDepAcc == null)
    {
        var accS = new ModuleSearcher<Account>();
        accS.AddEquals(e => e.Code.Value, "6300");
        var depAcc = accS.ExecuteFirstOrDefault();
        if (depAcc == null)
        {
            Toaster.Error("減価償却費の科目がありません（科目マスタの「役割」に depreciation_expense を設定してください）");
            return;
        }
        typedDepAcc = (Account)depAcc;
    }

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
    je.FixedAssetRef.Value = this.Id.Value;
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
