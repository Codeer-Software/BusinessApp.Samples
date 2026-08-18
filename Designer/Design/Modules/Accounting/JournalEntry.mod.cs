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
    var isPosted = !this.IsNewData && Status.Value == "posted";
    if (isPosted)
    {
        // 確定済み伝票は閲覧専用（訂正は赤黒訂正で行う）。
        // **`this.IsViewOnly = true`（モジュール全体）は使わない**——ボタンまで
        // `pointer-events: none` になって押せなくなり、「部門・プロジェクトを修正する」への
        // 入口が死ぬため（2026-08-14 実測。FB-035 と同じ現象）。画面に出る項目を個別にロックする。
        LockPostedFields();
        SaveDraftButton.IsVisible = false;
        PostButton.IsVisible = false;
    }
    // 部門・プロジェクトだけは確定後も直せる（ADR-0056）。入口はこのボタンだけで、
    // 修正はサブ画面（JournalLineDepartment）が担う——この明細グリッドは
    // 「下書きは全項目編集／確定済みは部門だけ編集」を切り替えられないため（レイアウトは設計時固定）
    DeptEditButton.IsVisible = isPosted;
    ShowLastUpdate();
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
    // 開いた時点の「科目と税区分の組」を控えに写す（BUG-0067）。これをしないと控えが空のままで、
    // 保存済みの下書きを開き直してから科目を変えたときに旧科目の税区分が残る
    if (!this.IsNewData)
    {
        inLinesHandler = true;
        SeedTaxCategoryTrace();
        inLinesHandler = false;
    }
    UpdateTotals();
}

// 「最終更新」を見せる（ADR-0056 決定 4 の監査証跡）。作成と同時刻なら出さない——
// 自動起票の伝票は一度も更新されないので、出ていること自体が「人が手で介入した印」になる。
// 更新者の表示名は AppUser を引き直す（LinkField の表示テキストは候補未ロードだと空になる）
void ShowLastUpdate()
{
    LastUpdateNote.Text = "";
    LastUpdateNote.IsVisible = false;
    if (this.IsNewData) { return; }
    if (UpdatedAt.Value == null || CreatedAt.Value == null) { return; }
    if (UpdatedAt.Value == CreatedAt.Value) { return; }

    var who = "";
    if (Updater.Value != null)
    {
        var s = new ModuleSearcher<AppUser>();
        s.AddEquals(u => u.Id.Value, Updater.Value);
        var found = s.ExecuteFirstOrDefault();
        if (found != null) { who = $" {((AppUser)found).表示名.Value}"; }
    }
    LastUpdateNote.Text = $"最終更新: {UpdatedAt.Value:yyyy/MM/dd HH:mm}{who}";
    LastUpdateNote.IsVisible = true;
}

// 確定済み伝票を閲覧専用にする。**画面に出る入力項目を漏れなく列挙すること**——
// モジュール全体の `IsViewOnly` を使えばこの列挙は要らないが、それだとボタンも死ぬ（上記）。
// 詳細レイアウトに載っている入力項目は 伝票番号・取引日・会計年度・伝票種別・摘要・明細 の 6 つ。
// 項目をレイアウトに足したらここにも足す（足し忘れると確定済み伝票が編集できてしまう）。
void LockPostedFields()
{
    JournalNo.IsViewOnly = true;
    EntryDate.IsViewOnly = true;
    FiscalYearRef.IsViewOnly = true;
    EntryType.IsViewOnly = true;
    FixedAssetRef.IsViewOnly = true;
    Description.IsViewOnly = true;
    Lines.IsViewOnly = true;
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

// 新規明細行への既定値: 貸借は借方、科目選択時に科目マスタの既定税区分と内税を設定。
//
// **税区分は科目に追従する。人が手で選んだ税区分は、その行の科目が変わるまで保持する**（BUG-0067）。
// 旧実装は `TaxCategory != null` なら一律に素通りしていたため、消耗品費を選んで「課税仕入 10%」が
// 自動で入ったあと同じ行の科目を普通預金に変えても税区分が残り、確定時に `RegenerateTaxLines` が
// 現預金を勝手に税抜化していた。借方合計と貸方合計は揃うので貸借エラーも出ず、実測で消費税集計表の
// 課税仕入を 1,000 円ぶん水増しした（伝票 No.94）。
//
// 実現のために、行ごとに「最後に見た科目と税区分の組」を 2 つの非 DB 項目に控える。
//   `TaxCategoryAutoFrom`  … そのときの科目 id
//   `TaxCategoryAutoValue` … そのときの税区分 id
// 判定はこの 3 通りだけ:
//   (1) 控えの科目 ≠ 現在の科目 → **科目が変わった。税区分を科目の既定で入れ直す**（補助科目も落とす）
//   (2) 控えの科目 = 現在の科目 かつ 控えの税区分 ≠ 現在の税区分 → **人が税区分を選び直した。
//       値はそのまま尊重し、控えだけ現在値へ進める**（次に科目が変わるまで保持される）
//   (3) それ以外 → 何もしない
//
// **DB を引くのは (1) と「税区分が未設定」のときだけ。** 控えの比較は文字列同士なので通信が要らない。
// `Lines_OnDataChanged` はセル編集ごと、かつスクリプトの代入ごとにも発火する（ADR-0053）ため、
// ここで無条件に `ModuleSearcher` を回すと一括起票が数十往復に膨らむ（CommonMistakes #56）。
//
// 控えは保存済み伝票を開いた直後に `SeedTaxCategoryTrace()` が現在値で埋める。
// これにより「下書きを開き直してから科目を変える」経路でも税区分が追従する。
void ApplyLineDefaults()
{
    var staleAccountIds = new List<object>();
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.Dc.Value == null || l.Dc.Value == "")
        {
            l.Dc.Value = "D";
        }
        if (l.Account.Value == null) continue;

        var accountKey = $"{l.Account.Value}";
        if (l.TaxCategory.Value != null && $"{l.TaxCategoryAutoFrom.Value}" == accountKey)
        {
            // (2)(3): 科目は変わっていない。税区分が動いていれば控えを進めるだけ（DB 不要）
            l.TaxCategoryAutoValue.Value = $"{l.TaxCategory.Value}";
            continue;
        }
        // (1) 科目が変わった／税区分が未設定。既定を引き直す対象
        if (!staleAccountIds.Contains(l.Account.Value))
        {
            staleAccountIds.Add(l.Account.Value);
        }
    }
    if (staleAccountIds.Count == 0) return;

    var s = new ModuleSearcher<Account>();
    s.AddIn(e => e.Id.Value, staleAccountIds);
    var accounts = s.Execute();

    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.Account.Value == null) continue;

        var accountKey = $"{l.Account.Value}";
        if (l.TaxCategory.Value != null && $"{l.TaxCategoryAutoFrom.Value}" == accountKey) continue;

        object defaultTaxCategory = null;
        foreach (var a in accounts)
        {
            var acc = (Account)a;
            if ($"{acc.Id.Value}" == accountKey)
            {
                defaultTaxCategory = acc.DefaultTaxCategory.Value;
                break;
            }
        }
        // 科目マスタの既定は ADR-0052 以降すべて埋まっている（ddl/490）。万一 NULL なら
        // 触らずに Submit 直前の保険（MarkRemainingLinesOutOfScope）へ委ねる
        if (defaultTaxCategory == null) continue;

        // 科目が変わった行は補助科目も前の科目のものが残っているので落とす
        // （SubAccount の候補は AccountId で絞られるが、既存値はクリアされないため）
        if ($"{l.TaxCategoryAutoFrom.Value}" != "" && $"{l.TaxCategoryAutoFrom.Value}" != accountKey)
        {
            l.SubAccount.Value = null;
        }

        l.TaxCategory.Value = defaultTaxCategory;
        if (l.TaxInputMode.Value == null || l.TaxInputMode.Value == "")
        {
            l.TaxInputMode.Value = "inclusive";
        }
        l.TaxCategoryAutoFrom.Value = accountKey;
        l.TaxCategoryAutoValue.Value = $"{defaultTaxCategory}";
    }
}

// 保存済み伝票を開いたときの控えの初期化。現在の「科目と税区分の組」をそのまま控えに写す。
// ＝**開いた時点の税区分は人が決めたものとして尊重し、科目を変えたときだけ追従させる**。
// これをしないと控えが空のままになり、下書きを開き直してからの科目変更で BUG-0067 が再現する。
void SeedTaxCategoryTrace()
{
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.Account.Value == null) continue;
        if (l.TaxCategory.Value == null) continue;
        l.TaxCategoryAutoFrom.Value = $"{l.Account.Value}";
        l.TaxCategoryAutoValue.Value = $"{l.TaxCategory.Value}";
    }
}

// 税区分が入っていない明細を「対象外」にする（ADR-0052）。**保険**として全経路が Submit() の
// 直前に呼ぶ。税区分は NOT NULL なので、万一 NULL のまま来ると DB エラーで落ちるのを防ぐ。
//
// **通常はここに来る行は無い。** `Lines_OnDataChanged` → `ApplyLineDefaults()` が
// スクリプトで作った伝票にも発火し、勘定科目マスタの既定税区分を先に入れてしまうため
// （2026-08-13 に実データで確認。ADR-0053 の「原因の訂正」を参照）。
// したがって **「税区分をセットしない＝対象外になる」わけではない**。内部振替のように
// 科目の既定が誤りになる伝票は `MarkAllLinesOutOfScope()` で明示的に上書きすること。
//
// 税行（IsTaxLine=true）は本体行と同じ税区分でなければならず、「対象外」を入れるとその税額が
// 消費税集計表から消える（B-5 の再発）ため、ここでは意図的に触らない。税区分の無い税行が
// 残れば DB の NOT NULL で落ちる＝呼び出し側のバグとして早期に表面化する。
// 内部振替の仕訳（減価償却・前受収益の按分振替など）の全明細を「対象外」に**上書きする**（ADR-0053）。
// **「税区分をセットしない」だけでは対象外にならない**のが要点。`Lines_OnDataChanged` →
// `ApplyLineDefaults()` は**スクリプトで作った伝票にも発火し**、勘定科目マスタの既定税区分を
// 自動で入れてしまう。実例: 減価償却の貸方（工具器具備品）に取得時の既定「課税仕入 10%」が入り、
// 消費税集計表の課税仕入を 114,375 円ぶん狂わせていた（デモ DB に実在した）。
// 内部振替は消費税が元の取引の時点で確定しているので、常に対象外が正しい。
void MarkAllLinesOutOfScope()
{
    var s = new ModuleSearcher<TaxCategory>();
    s.AddEquals(e => e.TaxationType.Value, "out_of_scope");
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return;
    var outOfScopeId = ((TaxCategory)found).Id.Value;

    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        l.TaxCategory.Value = outOfScopeId;
        l.TaxInputMode.Value = "none";
    }
}

// スクリプトから作った伝票に消費税行を生成する（下書きを経ずに確定まで進む経路用・ADR-0053）。
// **入力額（税込）のまま 1 回だけ呼ぶこと。** 税抜化済みの行に再度かけると二重に税抜化される
// ——`SaveEntry` が「税行の生成は確定時のみ」にしているのと同じ理由（下書き保存→確定、
// 確定失敗→再確定 の順路で必ず踏む罠だった）。
// `inLinesHandler` は `Lines_OnDataChanged` の再入ガードで、SaveEntry と同じ使い方をしている。
void GenerateTaxLinesOnce()
{
    inLinesHandler = true;
    RegenerateTaxLines();
    inLinesHandler = false;
}

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
    UpdateTaxHint();
}

// 外税の行があると、税額は確定時に税行として初めて追加されるため、入力中は差額が
// 税額分だけ残り続ける。内税に慣れた利用者には「差額が 0 にならないのに確定は通る」が
// 大きな驚きなので、追加される見込み額を差額欄の横に出す（改善候補 B-3）。
// マスタ検索は外税の行があるときだけ行う（Lines_OnDataChanged から毎回呼ばれるため）。
void UpdateTaxHint()
{
    var hasExclusive = false;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.TaxInputMode.Value != "exclusive") continue;
        if (l.TaxCategory.Value == null || l.Amount.Value == null) continue;
        hasExclusive = true;
        break;
    }
    if (!hasExclusive) { TaxHintLabel.Text = ""; return; }

    var catSearch = new ModuleSearcher<TaxCategory>();
    var rateSearch = new ModuleSearcher<TaxRate>();
    var batch = BatchSearcher.Execute(catSearch, rateSearch);
    var cats = batch.GetAt(0);
    var rates = batch.GetAt(1);

    // 経過措置の控除割合（税行に載るのは控除できる分だけ。RegenerateTaxLines と同じ解決）
    decimal transitionRate = 0;
    if (EntryDate.Value != null)
    {
        var trFirstDay = new DateTime(EntryDate.Value.Year, EntryDate.Value.Month, 1);
        var trSearch = new ModuleSearcher<InvoiceTransitionRate>();
        trSearch.AddLessThanOrEqual(e => e.ValidFrom.Value, trFirstDay);
        trSearch.AddGreaterThanOrEqual(e => e.ValidTo.Value, trFirstDay);
        var tr = trSearch.ExecuteFirstOrDefault();
        if (tr != null) { transitionRate = ((InvoiceTransitionRate)tr).RatePercent.Value ?? 0; }
    }

    var hint = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.TaxInputMode.Value != "exclusive") continue;
        if (l.TaxCategory.Value == null || l.Amount.Value == null) continue;
        foreach (var cItem in cats)
        {
            var cat = (TaxCategory)cItem;
            if ($"{cat.Id.Value}" != $"{l.TaxCategory.Value}") continue;
            var taxType = cat.TaxationType.Value;
            if (taxType != "taxable_sales" && taxType != "taxable_purchase") break;
            if (cat.Rate.Value == null) break;
            foreach (var rItem in rates)
            {
                var rate = (TaxRate)rItem;
                if ($"{rate.Id.Value}" != $"{cat.Rate.Value}") continue;
                decimal ratePercent = rate.RatePercent.Value ?? 0;
                int input = l.Amount.Value;
                int fullTax = input * ratePercent / 100;
                if (cat.UsesTransitionDeduction.Value == true) { fullTax = fullTax * transitionRate / 100; }
                hint = hint + fullTax;
                break;
            }
            break;
        }
    }
    if (hint > 0) { TaxHintLabel.Text = $"（確定時に消費税 {hint:#,0} 円が追加されます）"; }
    else { TaxHintLabel.Text = ""; }
}

void SaveDraft_OnClick()
{
    SaveEntry(false);
}

// 確定は不可逆（下書きへ戻す導線を持たない＝訂正は反対仕訳で行う）。
// 不可逆操作の確認ダイアログ規約（ADR-0059）に従い、押した瞬間に走らせない。
// LoadingService は MessageBox より後に開始する（オーバーレイがダイアログを覆う・実測）
void Post_OnClick()
{
    var answer = MessageBox.Show(
        "この伝票を確定します。確定すると下書きには戻せません。"
        + "訂正が必要になった場合は、反対仕訳を起票してください。",
        "確定する", "キャンセル");
    if (answer != "確定する") return;
    SaveEntry(true);
}

// 部門・プロジェクトの修正サブ画面へ（対象の伝票をクエリパラメータで渡す。ADR-0056）
void DeptEdit_OnClick()
{
    if (CurrentUser.HasAccountingAccess.Value != true)
    {
        Toaster.Error("仕訳の修正は経理のみ実行できます");
        return;
    }
    if (this.Id.Value == null) { return; }
    var url = NavigationService.GetModuleUrl("JournalLineDepartment");
    NavigationService.NavigateTo($"{url}?entry={this.Id.Value}");
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
        EntryDate.SetError("取引日の期間は締め済みです");
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

    // 損益科目の行には部門が要る（ADR-0056）。**確定のときだけ**止める——下書きは
    // 書きかけを保存できることに意味があるので、税行の生成と同じく確定時に初めて検証する
    if (post && !ValidateDepartments()) { return; }

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
            JournalNo.Value = NextJournalNo(FiscalYearRef.Value);
        }
        Status.Value = "posted";
    }

    // 部門は NOT NULL。空の行を全社共通で埋める（税行は親行から継ぐ）。
    // 税行生成のあとに呼ぶ必要があるのでここに置く（ADR-0056）
    //
    // ただし**収益行は全社共通で埋めない**（BUG-0266）。下書き保存で黙って埋まると、
    // 確定時の ValidateDepartments は「部門が入っている」と見て素通りし、売上が
    // 部門別 P/L から消える。post のときは上の ValidateDepartments が先に止めるので、
    // ここで弾かれるのは下書き保存だけ——書きかけでも収益行の部門だけは決めてもらう
    if (!TryFillMissingDepartments("明細の部門を選んでから保存してください")) { return; }

    var ret = this.Submit();
    if (ret == false)
    {
        // 確定は伝票番号を伴う＝**他の人が同時に確定すると番号が衝突して弾かれる**（ddl/610）。
        // 生の「UNIQUE constraint failed」を見せず、押し直せば通ることを伝える（欠番は許す方針）
        if (post) { Toaster.Error("伝票の確定に失敗しました。ほかの人が同時に伝票を確定した可能性があります。もう一度「確定する」を押してください"); }
        else { Toaster.Error("保存に失敗しました"); }
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

// 部門が入っていない明細を埋める（ADR-0056）。**保険**として全経路が Submit() の直前に呼ぶ。
// `department_id` は NOT NULL なので、万一 NULL のまま来ると DB エラーで落ちるのを防ぐ。
//
// 埋め方は 2 段。① 税行（IsTaxLine）は**親行（ParentLineNo）の部門を継ぐ**——税行は本体行の
// 付随物なので、部門も本体に従うのが正しい。② それでも空の行は「**全社共通**」にする。
//
// **ここは「入力を促す」役目を持たない。** 損益科目の行に正しい部門を入れさせるのは各画面の責任で、
// 人が画面にいる経路（振替伝票・入出金起票・仕入先請求書・銀行の一括起票）はエラーで止める。
// この関数は壊れたデータを DB に入れないための最後の砦であって、入力の正確さは UI が担う。
//
// **収益行にも全社共通を入れてしまう版**。売上が全社共通に入ると部門別 P/L に一切乗らないので
// （BUG-0266・BUG-0061）、部門の出どころがある経路は下の TryFillMissingDepartments を使うこと。
// こちらを残してあるのは「収益行の部門を決める材料がそもそも無い」経路のため:
//   - JournalImport（仕訳 CSV 取込。CSV に部門列が無い。BUG-0061 と合流して別途決める）
//   - SesBilling（SES 精算は案件・契約に部門ソースが無く、請求書の時点で全社共通を明示採用）
// なお Receipt・VendorInvoice・FixedAsset・経費精算の各経路は収益行を作らず、
// CashEntry・BankPosting は損益科目の行に部門が無ければ起票前に自分で止めるので、
// どちらを使っても結果は変わらない（現状維持のためこちらのまま）。
void FillMissingDepartments()
{
    var hasMissing = false;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.Department.Value == null) { hasMissing = true; break; }
    }
    if (!hasMissing) return;

    // ① 税行は親行から継ぐ
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value != true) continue;
        if (l.Department.Value != null) continue;
        if (l.ParentLineNo.Value == null) continue;
        foreach (var parentRow in Lines.Rows)
        {
            var p = (JournalLine)parentRow;
            if (p.IsTaxLine.Value == true) continue;
            if ($"{p.LineNo.Value}" != $"{l.ParentLineNo.Value}") continue;
            l.Department.Value = p.Department.Value;
            break;
        }
    }

    // ② 残りは全社共通（部門マスタの IsCommon フラグで解決する。コード直書きはしない）
    var s = new ModuleSearcher<Department>();
    s.AddEquals(e => e.IsCommon.Value, true);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return;
    var commonId = ((Department)found).Id.Value;

    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.Department.Value == null) { l.Department.Value = commonId; }
    }
}

// 部門を埋める版（BUG-0266）。**収益行だけは「全社共通」で埋めない。**
// 費用行の全社共通は「配賦の受け皿」として意味があるが、売上を全社共通に寄せると
// 部門別 P/L にその売上が一切乗らず、ペルソナの主要用途（部門別採算）が静かに壊れる。
// journal_lines.department_id は NOT NULL なので「空のまま保存」はできない。だから
// **収益行の部門が決まらないときは何も書き換えずに false を返し、保存自体を止める**。
//
// hint には「どこで部門を直せばよいか」を書く（画面ごとに直す場所が違うため）。
// 呼び出し側は false なら Submit せずに戻ること（エラー表示はこの中で済ませている）。
bool TryFillMissingDepartments(string hint)
{
    if (!ValidateRevenueDepartments(hint)) { return false; }
    FillMissingDepartments();
    return true;
}

// 収益科目の明細に部門が入っているかだけを見る（BUG-0266）。**何も書き換えない**——
// 途中まで埋めてから中断すると、画面に残る伝票の費用行にだけ全社共通が入った状態になり
// 「何が起きたのか」が読めなくなるため。税行は本体行から部門を継ぐので対象外。
bool ValidateRevenueDepartments(string hint)
{
    var accountIds = new List<object>();
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.Department.Value != null) continue;
        if (l.Account.Value == null) continue;
        if (!accountIds.Contains(l.Account.Value)) { accountIds.Add(l.Account.Value); }
    }
    if (accountIds.Count == 0) return true;

    var s = new ModuleSearcher<Account>();
    s.AddIn(e => e.Id.Value, accountIds);
    var accounts = s.Execute();
    foreach (var a in accounts)
    {
        var acc = (Account)a;
        // 動的値の直接比較は避け、いったん受けてから比べる（ValidateDepartments と同じ書き方）
        var t = acc.AccountType.Value;
        if (t != "revenue") continue;
        Toaster.Error($"収益科目（{acc.Name.Value}）の行に部門がありません。{hint}");
        return false;
    }
    return true;
}

// 損益科目（費用・収益）の明細に部門が入っているかを検証する（ADR-0056）。
// 人が画面にいる経路だけがこれを呼び、空ならエラーで止める。BS 科目は対象外（任意）。
bool ValidateDepartments()
{
    var accountIds = new List<object>();
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        if (l.Account.Value == null) continue;
        if (!accountIds.Contains(l.Account.Value)) { accountIds.Add(l.Account.Value); }
    }
    if (accountIds.Count == 0) return true;

    var s = new ModuleSearcher<Account>();
    s.AddIn(e => e.Id.Value, accountIds);
    var accounts = s.Execute();

    // 行番号は保存時に採番されるので、**入力中は LineNo が空**（実測 2026-08-17。
    // 「明細 行目（消耗品費）の部門を…」と数字が抜けたメッセージが出ていた）。
    // 画面に見えている並び順で数えて補う。税行は画面に出ないので数に含めない
    var displayNo = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.IsTaxLine.Value == true) continue;
        displayNo = displayNo + 1;
        if (l.Department.Value != null) continue;
        foreach (var a in accounts)
        {
            var acc = (Account)a;
            if ($"{acc.Id.Value}" != $"{l.Account.Value}") continue;
            var t = acc.AccountType.Value;
            if (t == "expense" || t == "revenue")
            {
                var lineLabel = $"{l.LineNo.Value}";
                if (lineLabel == "") { lineLabel = $"{displayNo}"; }
                Toaster.Error($"明細 {lineLabel} 行目（{acc.Name.Value}）の部門を選択してください（損益科目の行には部門が必要です）");
                return false;
            }
            break;
        }
    }
    return true;
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
            if ($"{cat.Id.Value}" == $"{l.TaxCategory.Value}")
            {
                taxType = cat.TaxationType.Value;
                if (cat.UsesTransitionDeduction.Value == true) { isTransition = true; }
                if (cat.Rate.Value != null)
                {
                    foreach (var rItem in rates)
                    {
                        var rate = (TaxRate)rItem;
                        if ($"{rate.Id.Value}" == $"{cat.Rate.Value}")
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

// 伝票番号の採番【正典】（BUG-0069）。**伝票を作る全経路がここを呼ぶ**——
// 他モジュールからは `new JournalEntry().NextJournalNo(年度Id)` で呼べる
// （モジュールをまたいだメソッド呼び出しは実証済み。Project.md 2026-07-26）。
//
// 方式は「その年度の最大 journal_no + 1」。読み取りと INSERT の間にロックは無いので、
// 同時起票では同じ番号を返しうる。**その衝突は DB の部分ユニークインデックスが弾く**
// （ddl/610。`journal_entries(fiscal_year_id, journal_no) WHERE journal_no IS NOT NULL`）。
// 弾かれた側は Submit() が false を返すだけで行は作られないので、押し直せば次の番号が取れる。
// **欠番は許す**（2026-08-17 ユーザー決定。税務上、伝票番号の連続は要件ではない）。
//
// 引数で年度を受けるのは、他モジュールが「まだ FiscalYearRef を入れていない新しい伝票」に
// 番号を振るため。自伝票の採番は NextJournalNo(FiscalYearRef.Value) と書く。
int NextJournalNo(object fiscalYearId)
{
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.FiscalYearRef.Value, fiscalYearId);
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
