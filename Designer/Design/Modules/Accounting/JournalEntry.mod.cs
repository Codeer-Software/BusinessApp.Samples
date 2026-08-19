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
    if (isPosted) { ShowReversalNote(); }
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
    // 取消（赤伝）の作成も確定済みのときだけ（ADR-0075）
    ReverseButton.IsVisible = isPosted;
    ShowLastUpdate();
    // 削除は「保存済みの下書き」だけ（確定済み伝票は削除不可＝赤黒訂正で消し込む。ADR-0026）
    DeleteDraftButton.IsVisible = !this.IsNewData && (Status.Value == "draft");
    if (!this.IsNewData && Status.Value == "draft"
        && SourceType.Value != "import" && SourceType.Value != "reversal")
    {
        // 旧仕様の下書き（税抜変換済み・税行あり）を入力状態へ戻す。
        // 現仕様の下書きは生の入力のまま保存されるので通常は no-op。
        // CSV インポート由来（source_type='import'）と取消の赤伝（'reversal'・ADR-0075）は
        // **税行込みの生データが正**なので畳まない——畳むと税行が消えて貸借が崩れる（実測）。
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

// 貸借一致の検証（BUG-0068）。**他モジュールが `je.Submit()` する直前に必ず呼ぶ**。
// 一致していれば ""、していなければ人が読める理由を返す。
//
// なぜ共有ヘルパにするのか: 「貸借一致しない伝票は確定できない」（docs/04 §0-2）は
// このアプリの一番外側の約束なのに、長らく `JournalEntry` の確定と CSV 取込の 2 経路でしか
// 守られていなかった。自動起票（入金・検収・経費・償却・銀行・定期請求 …）は
// 金額を自分で組み立てて `Submit()` していて、組み立てを間違えても誰も気づけない。
// SQLite 側にも制約は置けない（親と明細は別 INSERT なので、行が揃う瞬間が無い）。
// **確定の直前にアプリ層で 1 回見る**のが唯一の止め場所である。
//
// 呼ぶのは `Submit()` の前。ここで止めれば伝票そのものが生まれないので、
// 「途中まで書けてしまった伝票をどう巻き戻すか」（BUG-0131／0148）の論点に立ち入らずに済む。
string ValidateBalanced()
{
    var d = 0;
    var c = 0;
    var n = 0;
    foreach (var row in Lines.Rows)
    {
        var l = (JournalLine)row;
        if (l.Amount.Value == null) continue;
        if (l.Dc.Value == "D") { d = d + l.Amount.Value; n = n + 1; }
        else if (l.Dc.Value == "C") { c = c + l.Amount.Value; n = n + 1; }
    }
    if (n == 0) { return "仕訳明細が 1 行もありません"; }
    if (d != c) { return $"貸借が一致していません（借方 {d:#,0} 円 / 貸方 {c:#,0} 円 / 差額 {d - c:#,0} 円）"; }
    return "";
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
    // **年度の締めも見る**（BUG-0100）。期間だけを見ていると、年次決算を終えて年度を締めたあとに
    // 1 か月だけ期間を再オープンした隙に、その年度へ確定仕訳を作れてしまう。
    // 締めガードの粒度は `FixedAsset` の先例（年度 closed を明示的に止める）に揃える
    if (IsFiscalYearClosed(FiscalYearRef.Value))
    {
        EntryDate.SetError("取引日の会計年度は締め済みです（期間を再オープンしても年度が締まっていれば起票できません）");
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
    // **無加工で確定する伝票**（税抜化も税行生成もしない）。
    //   import   … 移行元と 1 円もズレない保証（既存）
    //   reversal … 元伝票の完全な鏡像。税行まで写してあるので触ってはいけない（ADR-0075）
    var isRawImport = (SourceType.Value == "import" || SourceType.Value == "reversal");
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
    // 科目は**役割で引き、無ければコードで拾う**（BUG-0446）。
    // ddl/820 で仮払消費税に `consumption_tax_receivable`、ddl/700 で仮受消費税に
    // `consumption_tax_payable` を割り当てている。科目体系を組み替えた導入先
    // （＝「テンプレートに少し手を加えて使う」という本アプリの売り文句そのもの）でコードが変わると、
    // 税行の Account が null のまま NOT NULL に当たって Submit が false になり、
    // ユーザーには「ほかの人が同時に伝票を確定した可能性があります」という**無関係な文言**しか出ない。
    // AddIn を消さずコードも併記するのは、役割が未設定の環境でも従来どおり動かすため
    accSearch.AddIn(e => e.Code.Value, "1900", "2200");
    var batch = BatchSearcher.Execute(catSearch, rateSearch, accSearch);
    var cats = batch.GetAt(0);
    var rates = batch.GetAt(1);
    var taxAccounts = batch.GetAt(2);

    object purchaseTaxAccountId = ResolveTaxAccountId("consumption_tax_receivable");
    object salesTaxAccountId = ResolveTaxAccountId("consumption_tax_payable");
    foreach (var a in taxAccounts)
    {
        var acc = (Account)a;
        if (purchaseTaxAccountId == null && acc.Code.Value == "1900") { purchaseTaxAccountId = acc.Id.Value; }
        if (salesTaxAccountId == null && acc.Code.Value == "2200") { salesTaxAccountId = acc.Id.Value; }
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
// 会計年度が締まっているか（BUG-0100 の規則の正典）。
// **年度が正・期間はその下位**。決算修正のために 1 か月だけ期間を再オープンしても、
// 年度が closed なら起票させない。年度を締めた時点で翌期の期首残高が確定しているので、
// あとから当期に仕訳を足すと翌期の期首とずれる（BUG-0060）。
//
// 確定仕訳を作る経路は**すべてここを通す**（BUG-0442）。
// 仕訳インポート・固定資産の処分／処分取消・仕掛品の期末振替が素通りしていた
// 消費税科目を役割で引く（BUG-0446）。未設定なら null を返し、呼び元がコードで拾い直す
object ResolveTaxAccountId(string role)
{
    var s = new ModuleSearcher<Account>();
    s.AddEquals(e => e.AccountRole.Value, role);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return ((Account)found).Id.Value;
}

// 仕訳を明細ごと削除する（正典・BUG-0148）。成功なら ""、失敗ならユーザーに出すメッセージを返す。
//
// **CLB のスクリプトにトランザクション境界は無い。** 明細を 1 行ずつ消している途中で失敗すると、
// 借方だけ／貸方だけの伝票が posted のまま帳簿に残り、試算表・総勘定元帳の貸借が合わなくなる。
// 親単独の `Delete()` は子がいると静かに失敗する（実測）ので、行ごと削除は避けられない。
//
// 方針（ADR-0077）: **消し始める前に「消せるか」を確かめる。**
//   `journal_lines` を参照している表は無い（`Designer/ddl/` を全数確認）。
//   したがって削除が途中で落ちる現実的な原因は **この伝票を指している他の表**——
//   銀行明細（`bank_statement_lines.journal_entry_id`）と
//   仕入先請求の支払リンク（`vendor_invoices.payment_entry_id`）の 2 つだけ。
//   呼び元はどちらも「先に参照を外してから」呼ぶ約束なので、ここで残っていたら**呼び元の順序が誤り**。
//   先に断れば、貸借の欠けた伝票を作らずに済む。
//
//   それでも途中で失敗したら（DB エラー等）、**伝票番号と残った状態を名指しで伝える**。
//   この状態は不変条件 `A01_仕訳_伝票ごとの貸借一致` が拾うので、気づかないまま流れることはない。
string DeleteWithLines()
{
    var no = JournalNo.Value;

    var bs = new ModuleSearcher<BankStatementLine>();
    bs.AddEquals(e => e.JournalEntryId.Value, this.Id.Value);
    if (bs.Execute().Count > 0)
    {
        return $"伝票 No.{no} は銀行明細から参照されているため削除できません（先に明細側の起票を取り消してください）";
    }

    var vs = new ModuleSearcher<VendorInvoice>();
    vs.AddEquals(e => e.PaymentEntryId.Value, this.Id.Value);
    if (vs.Execute().Count > 0)
    {
        return $"伝票 No.{no} は仕入先請求の支払リンクから参照されているため削除できません（先に支払を取り消してください）";
    }

    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(l => l.JournalEntryId.Value, this.Id.Value);
    var lines = ls.Execute();
    var removed = 0;
    foreach (var row in lines)
    {
        var l = (JournalLine)row;
        if (l.Delete() != true)
        {
            return $"伝票 No.{no} の明細削除が {removed} 行目までで失敗しました。"
                + $"**この伝票は貸借が合っていない状態で帳簿に残っています**（残り {lines.Count - removed} 行）。"
                + "経理に連絡し、この伝票番号を伝えてください";
        }
        removed = removed + 1;
    }

    if (this.Delete() != true)
    {
        return $"伝票 No.{no} の明細はすべて削除しましたが、伝票本体を削除できませんでした。"
            + "**明細の無い空の伝票が残っています**。経理に連絡し、この伝票番号を伝えてください";
    }
    return "";
}

bool IsFiscalYearClosed(object fiscalYearId)
{
    if (fiscalYearId == null) return false;
    var s = new ModuleSearcher<FiscalYear>();
    s.AddEquals(e => e.Id.Value, fiscalYearId);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return false;
    return ((FiscalYear)found).Status.Value == "closed";
}

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

// 確定済み伝票の「取消（赤伝）」を下書きで作る（BUG-0073・ADR-0075）。
//
// なぜ機械化するのか: 確定伝票は**税抜化済み**（本体 50,000 ＋ 仮払消費税 5,000）で保存されている。
// 手で赤伝を切るときの正解は「**税込 55,000 を内税で入れ直す**」で、画面に出ている本体額 50,000 を
// そのまま入れると再度税抜化されて 45,455 ＋ 4,545 になり、元伝票と 1 円単位で合わない。
// 正解を知っていないと当てられないうえ、税込額は画面のどこにも出ていない（足し戻すしかない）。
//
// このボタンは**全行を D/C 反転してそのまま複製する**——本体行も税行も、
// 部門・案件・税区分・摘要も含めて。`TaxInputMode` は全行 `none` にし、
// `SourceType = "reversal"` で確定時の税行再生成を止める（`SaveEntry` の `isRawImport` と同じ扱い）。
// これで**税まで含めた完全な鏡像**になる。
//
// 作るのは**下書き**。日付も摘要も直せるし、要らなければ削除できる——
// 「確定は不可逆」（ADR-0026）を崩さないための線引きである。

// この伝票を取り消した赤伝があれば、案内文に書き足す（ADR-0075 の副産物）。
//
// 赤伝は `source_type='reversal'` / `source_id=元伝票の id` で機械的に繋がっている。
// 繋がりが無かった頃は「摘要が唯一の手がかり」で、
// **元伝票だけを開いた人には取り消されたことが分からなかった**。
// 帳簿を読む側にとっては「この数字はもう生きていない」が最初に要る情報なので、上に出す。
void ShowReversalNote()
{
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.SourceType.Value, "reversal");
    s.AddEquals(e => e.SourceId.Value, this.Id.Value);
    s.OrderBy(e => e.Id.Value);
    var rows = s.Execute();
    if (rows.Count == 0) return;

    var nos = new List<string>();
    var hasDraft = false;
    foreach (var row in rows)
    {
        var r = (JournalEntry)row;
        if (r.Status.Value == "posted") { nos.Add($"No.{r.JournalNo.Value}"); }
        else { hasDraft = true; }
    }

    if (nos.Count > 0)
    {
        PostedNote.Text = $"⚠ この伝票は {string.Join(" / ", nos)} の取消（赤伝）で打ち消されています。"
            + "帳簿上の残高には反映済みです（元伝票と赤伝の両方が帳簿に残るのが赤黒訂正の形）。";
        return;
    }
    if (hasDraft)
    {
        PostedNote.Text = "この伝票の取消（赤伝）が下書きのまま残っています。"
            + "確定するまで打ち消しは帳簿に反映されません（振替伝票の一覧で下書きを探してください）。";
    }
}

void Reverse_OnClick()
{
    if (this.IsNewData || Status.Value != "posted")
    {
        Toaster.Error("確定済みの伝票だけ取消（赤伝）を作れます");
        return;
    }

    // **二重の赤伝を止める**。元 + 赤伝 = 0 なので、もう 1 本切ると
    // 「元伝票をもう一度マイナスで立てた」のと同じになり、帳簿が元伝票 1 本ぶん狂う。
    // 下書きが残っているだけなら、新しく作らずにそれを開く（作りかけを増やさない）
    var exS = new ModuleSearcher<JournalEntry>();
    exS.AddEquals(e => e.SourceType.Value, "reversal");
    exS.AddEquals(e => e.SourceId.Value, this.Id.Value);
    exS.OrderByDescending(e => e.Id.Value);
    var exRows = exS.Execute();
    object draftId = null;
    foreach (var row in exRows)
    {
        var r = (JournalEntry)row;
        if (r.Status.Value == "posted")
        {
            Toaster.Error($"この伝票は既に No.{r.JournalNo.Value} の取消（赤伝）で打ち消されています。二重に取り消すと帳簿が狂います");
            return;
        }
        if (draftId == null) { draftId = r.Id.Value; }
    }
    if (draftId != null)
    {
        Toaster.Warn("この伝票の取消（赤伝）は既に下書きで作ってあります。その下書きを開きます");
        var durl = NavigationService.GetModuleUrl("JournalEntry");
        NavigationService.NavigateTo($"{durl}/{draftId}");
        return;
    }

    // 元伝票の明細を DB から取り直す（メモリ行の遅延ロード対策）
    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(e => e.JournalEntryId.Value, this.Id.Value);
    ls.OrderBy(e => e.LineNo.Value);
    var srcLines = ls.Execute();
    if (srcLines.Count == 0) { Toaster.Error("元伝票の明細が読み込めませんでした"); return; }

    // 取消の日付は**今日**。元伝票と同じ日にしたいときは下書きのまま直せる
    // （元の月が締め済みのことがあるので、既定を元日付にはしない）
    var today = DateOnly.FromDateTime(DateTime.Today);
    var monthFirst = new DateOnly(today.Year, today.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { Toaster.Error("本日に対応する会計年度がありません"); return; }

    using var loading = LoadingService.StartLoading(0);

    var je = new JournalEntry();
    je.EntryDate.Value = today;
    je.EntryType.Value = EntryType.Value;
    je.Description.Value = $"No.{JournalNo.Value} 取消（赤伝）";
    je.Status.Value = "draft";
    je.FiscalYearRef.Value = ((FiscalYear)fy).Id.Value;
    je.SourceType.Value = "reversal";
    je.SourceId.Value = this.Id.Value;
    je.PartnerRef.Value = PartnerRef.Value;  // 電帳法の検索要件（取引先で探せること・BUG-0003）（元伝票から引き継ぐ）
    je.Lines.AddRows(srcLines.Count);

    var i = 0;
    foreach (var row in je.Lines.Rows)
    {
        var dst = (JournalLine)row;
        var src = (JournalLine)srcLines[i];
        i = i + 1;
        dst.LineNo.Value = i;
        dst.Dc.Value = (src.Dc.Value == "D") ? "C" : "D";   // ここが赤伝の本体
        dst.Account.Value = src.Account.Value;
        dst.SubAccount.Value = src.SubAccount.Value;
        dst.Department.Value = src.Department.Value;
        dst.ProjectRef.Value = src.ProjectRef.Value;
        dst.TaxCategory.Value = src.TaxCategory.Value;
        dst.TaxInputMode.Value = "none";                    // 再度の税抜化をさせない
        dst.IsTaxLine.Value = src.IsTaxLine.Value;
        dst.ParentLineNo.Value = src.ParentLineNo.Value;
        dst.Amount.Value = src.Amount.Value;
        dst.InputAmount.Value = src.Amount.Value;
        dst.Description.Value = src.Description.Value;
    }

    // 貸借一致の検証（BUG-0068）。鏡像なので通るはずだが、通らないなら元伝票が壊れている
    var imbalance = je.ValidateBalanced();
    if (imbalance != "")
    {
        Toaster.Error($"取消伝票を作れませんでした（{imbalance}）。元伝票の明細を確認してください");
        return;
    }
    if (je.Submit() != true) { Toaster.Error("取消伝票の作成に失敗しました"); return; }

    Toaster.Success($"No.{JournalNo.Value} の取消（赤伝）を下書きで作りました。日付と摘要を確認して確定してください");

    var newId = FindReversalId(this.Id.Value);
    if (newId != null)
    {
        var url = NavigationService.GetModuleUrl("JournalEntry");
        NavigationService.NavigateTo($"{url}/{newId}");
    }
}

// いま作った取消伝票の id を DB から引く（`Submit()` 後のインスタンスの Id は信用しない）
object FindReversalId(object srcId)
{
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.SourceType.Value, "reversal");
    s.AddEquals(e => e.SourceId.Value, srcId);
    s.OrderByDescending(e => e.Id.Value);
    s.Limit(1);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return ((JournalEntry)found).Id.Value;
}

void DeleteDraft_OnClick()
{
    if (this.IsNewData) { Toaster.Error("保存されていない伝票です"); return; }
    if (Status.Value != "draft") { Toaster.Error("下書きの伝票のみ削除できます（確定済みは赤黒訂正で対応してください）"); return; }

    // **締めのガードは削除にも要る**（BUG-0074）。保存側（`SaveEntry`）には年度・期間の
    // チェックがあり、`JournalLineDepartment` も締め済みを見ているのに、**この経路だけ抜けていた**。
    // 締めた期間に残っている下書きを消せると、締め時点で数えた「未確定の伝票」が後から変わる
    if (IsFiscalYearClosed(FiscalYearRef.Value))
    {
        Toaster.Error("この伝票の会計年度は締め済みです（年度の締めを解除してから削除してください）");
        return;
    }
    if (EntryDate.Value != null)
    {
        var dMonth = new DateOnly(EntryDate.Value.Year, EntryDate.Value.Month, 1);
        var dps = new ModuleSearcher<FiscalPeriod>();
        dps.AddLessThanOrEqual(e => e.StartDate.Value, dMonth);
        dps.AddGreaterThanOrEqual(e => e.EndDate.Value, dMonth);
        var dp = dps.ExecuteFirstOrDefault();
        if (dp != null && ((FiscalPeriod)dp).Status.Value == "closed")
        {
            Toaster.Error("取引日の期間は締め済みです（期間を再オープンしてから削除してください）");
            return;
        }
    }

    var result = MessageBox.Show("この下書き伝票を削除しますか？（元に戻せません）", "削除する", "キャンセル");
    if (result != "削除する") return;
    using var loading = LoadingService.StartLoading(0);
    // **戻り値を検査する**（BUG-0082）。`Lines` は `DeleteTogether: true` の子を持つので、
    // 子側の削除が失敗すると `Delete()` は false を返して静かに終わる。
    // 無条件に成功トーストを出して一覧へ遷移すると、**残っていても目に入らない**
    if (this.Delete() != true)
    {
        Toaster.Error("下書き伝票を削除できませんでした。画面を開き直してからもう一度お試しください");
        return;
    }
    Toaster.Success("下書き伝票を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("JournalEntry"));
}
