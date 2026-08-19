// CashEntry.mod.cs — 入出金起票（表示専用モジュール・経理専用）
// 責務: 現預金の入出金を**下書きに積み、まとめて仕訳にする**（ADR-0055）。
//        銀行明細取込（ADR-0012）と同じステージング方式で、下書き 1 行 = 仕訳 1 本。
//        採番は一括起票の実行時。入金: D 現預金 / C 相手科目、出金: D 相手科目 / C 現預金。
//        相手科目が課税区分なら金額を税込として扱い、消費税行まで作る（ADR-0053）。
//        source_type='cashbook' で出所を記録（source_id は無し）。
//
// 下書きは「打ちかけているメモ」＝個人の作業領域なので、**自分の行だけ**を見る
// （DraftList の検索条件が Creator = CurrentUser。銀行明細＝会社の共有物とはここが違う）。

// PostOne が返す消費税額の受け渡し用。**`ref`/`out` 引数は CLB のスクリプトでは呼び出し元へ
// 伝わらない**（実測 2026-08-14: 値がゼロのまま戻り、結果表示から「うち消費税」が消えた。
// 例外は出ないので静かに壊れる）。モジュールレベル変数で受け渡す。
int lastPostedTax = 0;

void Detail_OnAfterInit()
{
    if (Direction.Value == null || Direction.Value == "") { Direction.Value = "in"; }

    // 既定値は「自分の直近の下書き」から引く（毎回同じ科目を選び直さずに済む。
    // 旧実装は普通預金(1020)を JSON に直書きしていたが、マスタ化にあわせて廃止した）
    var s = new ModuleSearcher<CashEntryDraft>();
    s.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    s.OrderByDescending(e => e.Id.Value);
    s.Limit(1);
    var last = s.ExecuteFirstOrDefault();
    if (last != null)
    {
        var typedLast = (CashEntryDraft)last;
        if (EntryDate.Value == null) { EntryDate.Value = typedLast.EntryDate.Value; }
        if (CashAccount.Value == null) { CashAccount.Value = typedLast.CashAccount.Value; }
    }
    if (EntryDate.Value == null) { EntryDate.Value = DateOnly.FromDateTime(DateTime.Today); }

    RefreshSummary();
}

// ============ 入力欄 → 下書きに 1 行積む ============

void Add_OnClick()
{
    if (!IsAccounting()) { return; }
    if (!ValidateInputRow()) { return; }

    using var loading = LoadingService.StartLoading(0);

    var draft = new CashEntryDraft();
    draft.EntryDate.Value = EntryDate.Value;
    draft.CashAccount.Value = CashAccount.Value;
    draft.Direction.Value = Direction.Value;
    draft.CounterAccount.Value = CounterAccount.Value;
    draft.DepartmentRef.Value = DepartmentRef.Value;
    draft.Amount.Value = Amount.Value;
    draft.Description.Value = Description.Value;
    var ret = draft.Submit();
    if (ret != true) { Toaster.Error("下書きの追加に失敗しました"); return; }

    // 連続入力: 金額と摘要だけ消し、日付・科目・入出金・部門は残す
    Amount.Value = null;
    Description.Value = null;

    DraftList.Reload();
    RefreshSummary();
    ResultLabel.Text = "";
    Toaster.Success("下書きに追加しました");
    Amount.Focus();
}

// 入力欄 1 行分の検証。エラーはトースト（表示専用モジュールなのでフィールドエラーを持たない）
bool ValidateInputRow()
{
    if (EntryDate.Value == null) { Toaster.Error("取引日を入力してください"); return false; }
    if (CashAccount.Value == null) { Toaster.Error("現預金科目を選択してください"); return false; }
    if (Direction.Value == null || Direction.Value == "") { Toaster.Error("入出金を選択してください"); return false; }
    if (CounterAccount.Value == null) { Toaster.Error("相手科目を選択してください"); return false; }
    if (Amount.Value == null || Amount.Value <= 0) { Toaster.Error("金額を入力してください"); return false; }

    // 現預金科目そのものを相手科目にすると、増減の相殺した無意味な仕訳になる
    // （現預金どうしの振替＝小口現金の補充などは、別の現預金科目を選べば作れる）
    if ($"{CounterAccount.Value}" == $"{CashAccount.Value}")
    {
        Toaster.Error("相手科目に同じ現預金科目は選べません");
        return false;
    }

    // 損益科目の行には部門が要る（ADR-0056。人が画面にいる経路はエラーで止める）
    if (IsProfitLossAccount(CounterAccount.Value) && DepartmentRef.Value == null)
    {
        Toaster.Error("部門を選択してください（損益科目の仕訳には部門が必要です）");
        return false;
    }
    return true;
}

bool IsAccounting()
{
    if (CurrentUser.HasAccountingAccess.Value == true) { return true; }
    Toaster.Error("入出金の起票は経理のみ実行できます");
    return false;
}

bool IsProfitLossAccount(object accountId)
{
    if (accountId == null) { return false; }
    var s = new ModuleSearcher<Account>();
    s.AddEquals(e => e.Id.Value, accountId);
    var acc = s.ExecuteFirstOrDefault();
    if (acc == null) { return false; }
    var t = ((Account)acc).AccountType.Value;
    return t == "expense" || t == "revenue";
}

// ============ グリッドの編集を保存する ============

void Save_OnClick()
{
    if (!IsAccounting()) { return; }
    using var loading = LoadingService.StartLoading(0);
    var removed = SaveListEdits();
    DraftList.Reload();
    RefreshSummary();
    if (lastSaveHadFailure)
    {
        Toaster.Error("保存できなかった行があります。画面の入力内容を確認してください"
            + "（保存できた行だけが反映されています。行の削除は見送りました）");
        return;
    }
    if (removed < 0)
    {
        Toaster.Warn($"編集内容は保存しましたが、行の削除は反映していません。"
            + $"下書きが {DraftPageLimit()} 件を超えていて画面に全部載っていないため、"
            + "表示していないページの下書きまで消してしまう恐れがあります。"
            + "起票または破棄で件数を減らしてから削除してください");
        return;
    }
    if (removed > 0) { Toaster.Success($"変更を保存しました（{removed} 行を削除）"); }
    else { Toaster.Success("変更を保存しました"); }
}

// DraftList が 1 ページに載せる件数。**DraftList.SearchCondition.LimitCount と対で保守する。**
// この値を超えると ListField はページを割り、`Rows` は「現在のページの行」しか返さなくなる。
int DraftPageLimit()
{
    return 200;
}

// 画面の下書き行を DB に書き戻す。**画面から消えた行は DB からも消す**
// （ListField の行削除はメモリ上の操作なので、ここで確定させないと再読込で復活する）。
//
// ただし `DraftList.Rows` が返すのは**現在のページの行だけ**である（CLB 仕様）。
// 下書きが 1 ページに収まらないと、表示していないページの行まで「画面から消えた行」に見え、
// 未起票の下書きが無言で全滅する（下書きに履歴は無く復旧できない）。
// **ロードしていない行は消さない。** 収まらないときは削除を行わず -1 を返す（呼び元が知らせる）。
//
// 戻り値: 削除した行数。-1 = ページが割れているため削除を見送った。
// 戻り値の 2 つ目の意味: 保存に失敗した行があれば false（呼び元は先へ進まない）。
// **`Submit()` の戻り値を捨てない**（BUG-0445）。ここを黙って通すと、一括起票の直前に走る経路で
// **DB に残っている古い金額のまま確定仕訳が起票される**——このメソッドが防ぐはずだったことそのもの。
// 同型は `JournalLineDepartment` が「保存されたと信じて離脱するのが一番まずい壊れ方」として
// 既に対処済みで、こちらだけ作法が割れていた
bool lastSaveHadFailure = false;

int SaveListEdits()
{
    lastSaveHadFailure = false;
    var aliveIds = new List<string>();
    foreach (var row in DraftList.Rows)
    {
        var t = (CashEntryDraft)row;
        if (t.Submit() != true) { lastSaveHadFailure = true; continue; }
        aliveIds.Add($"{t.Id.Value}");
    }
    // 失敗した行を aliveIds に入れないと「画面から消えた行」と誤認して削除してしまう。
    // 失敗があったら削除自体を見送る（下書きに履歴は無く、消すと復旧できない）
    if (lastSaveHadFailure) { return -1; }

    var s = new ModuleSearcher<CashEntryDraft>();
    s.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    var all = s.Execute();
    if (all.Count > DraftPageLimit()) { return -1; }

    var removed = 0;
    foreach (var row in all)
    {
        var t = (CashEntryDraft)row;
        if (aliveIds.Contains($"{t.Id.Value}")) continue;
        if (t.Delete() == true) { removed = removed + 1; }
    }
    return removed;
}

// ============ 下書きをすべて破棄 ============

void DiscardAll_OnClick()
{
    if (!IsAccounting()) { return; }
    var s = new ModuleSearcher<CashEntryDraft>();
    s.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    var all = s.Execute();
    if (all.Count == 0) { Toaster.Info("破棄する下書きがありません"); return; }

    var answer = MessageBox.Show($"未起票の下書き {all.Count} 件をすべて破棄します。よろしいですか？（元に戻せません）", "破棄する", "キャンセル");
    if (answer != "破棄する") { return; }

    using var loading = LoadingService.StartLoading(0);
    var deleted = 0;
    foreach (var row in all)
    {
        var t = (CashEntryDraft)row;
        if (t.Delete() == true) { deleted = deleted + 1; }
    }
    DraftList.Reload();
    RefreshSummary();
    ResultLabel.Text = "";
    Toaster.Success($"下書き {deleted} 件を破棄しました");
}

// ============ まとめて起票する ============

void PostAll_OnClick()
{
    if (!IsAccounting()) { return; }

    // 一括で確定仕訳を作る操作なので確認する（ADR-0062）
    var cs0 = new ModuleSearcher<CashEntryDraft>();
    cs0.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    var draftCount = cs0.Execute().Count;
    if (draftCount == 0) { Toaster.Info("起票する下書きがありません"); return; }

    // 1 ページに収まらないときは、画面で消した行の削除を確定できない（SaveListEdits 参照）。
    // 「消したはずの行が起票された」を後から取り消すのは高くつくので、先に伝えて選ばせる
    var caution = "";
    if (draftCount > DraftPageLimit())
    {
        caution = $"\n\n※ 下書きが {DraftPageLimit()} 件を超えていて画面に全部載っていません。"
            + "この画面で削除した行があっても、その削除は反映されず起票されます。";
    }
    var answer = MessageBox.Show(
        $"下書き {draftCount} 件を一括で起票します（確定仕訳として帳簿に載ります）。よろしいですか？{caution}",
        "起票する", "キャンセル");
    if (answer != "起票する") return;

    using var suspend = this.SuspendNotifyStateChanged();
    using var loading = LoadingService.StartLoading(0);

    // 画面の編集内容（と削除）を先に確定させてから起票する。
    // そうしないと「直したつもりの金額」で起票されたり、消したはずの行が起票される
    SaveListEdits();
    // **保存に失敗したまま起票しない**（BUG-0445）。ここから先は DB を読み直して起票するので、
    // 画面で直した金額が保存できていないと**古い金額で確定仕訳が立つ**
    if (lastSaveHadFailure)
    {
        Toaster.Error("保存できなかった行があるため、起票を中止しました。"
            + "画面の入力内容を確認してから、もう一度実行してください");
        return;
    }

    var s = new ModuleSearcher<CashEntryDraft>();
    s.AddEquals(e => e.Creator.Value, CurrentUser.Id.Value);
    s.OrderBy(e => e.EntryDate.Value);
    s.OrderBy(e => e.Id.Value);
    var drafts = s.Execute();
    if (drafts.Count == 0) { Toaster.Info("起票する下書きがありません"); return; }

    var posted = 0;
    var skipped = 0;
    var failed = 0;
    var leftovers = 0;
    var leftoverNos = new List<string>();
    var reasons = new List<string>();
    var postedNos = new List<string>();
    var totalTax = 0;

    foreach (var row in drafts)
    {
        var draft = (CashEntryDraft)row;
        var reason = ValidateDraft(draft);
        if (reason != "")
        {
            skipped = skipped + 1;
            if (!reasons.Contains(reason)) { reasons.Add(reason); }
            continue;
        }

        lastPostedTax = 0;
        var no = PostOne(draft);
        if (no <= 0) { failed = failed + 1; continue; }

        posted = posted + 1;
        totalTax = totalTax + lastPostedTax;
        postedNos.Add($"{no}");
        // **削除の失敗を見逃さない**（BUG-0102）。仕訳の Submit は成功したのに Delete が失敗すると、
        // 下書きが残ったまま結果表示は「起票 N 件」になる。利用者は残った行を見て
        // **もう一度「一括起票」を押す**——同じ入出金が 2 本の確定仕訳になり、
        // `source_type='cashbook'` は `source_id` を持たない（ADR-0055）ので
        // **機械的に重複を検出する手掛かりが無い**
        if (draft.Delete() != true)
        {
            leftovers = leftovers + 1;
            leftoverNos.Add($"{no}");
        }
    }

    DraftList.Reload();
    RefreshSummary();

    // 起票できたのに下書きが消えなかった行は、**必ず名指しで伝える**（BUG-0102）。
    // 黙っていると「まだ残っている＝起票されていない」と読まれて二重起票になる
    if (leftovers > 0)
    {
        Toaster.Error($"仕訳は作れましたが、下書きが {leftovers} 行残りました"
            + $"（伝票 No.{string.Join(", ", leftoverNos)}）。"
            + "この行はもう起票済みです。「一括起票」を押し直さないでください——"
            + "残った行は行の削除で片付けてから「変更を保存」してください");
    }

    var noText = "";
    if (postedNos.Count > 0) { noText = $"（No.{postedNos[0]}〜{postedNos[postedNos.Count - 1]}）"; }
    var taxText = "";
    if (totalTax > 0) { taxText = $" ／ うち消費税 {totalTax:#,0} 円"; }
    var reasonText = "";
    if (reasons.Count > 0) { reasonText = $" ［{string.Join(" / ", reasons)}］"; }

    ResultLabel.Text = $"起票 {posted} 件{noText}{taxText} ／ スキップ {skipped} 件{reasonText} ／ 失敗 {failed} 件";
    if (posted > 0) { Toaster.Success($"{posted} 件を起票しました{noText}"); }
    else if (skipped > 0) { Toaster.Warn("起票できる下書きがありませんでした（スキップの理由を確認してください）"); }
    else { Toaster.Error("起票に失敗しました"); }
}

// 起票できない理由を返す（空文字なら起票できる）。締め済み等は「その行だけ」スキップする
string ValidateDraft(CashEntryDraft draft)
{
    if (draft.EntryDate.Value == null) { return "取引日なし"; }
    if (draft.CashAccount.Value == null) { return "現預金科目なし"; }
    if (draft.CounterAccount.Value == null) { return "相手科目なし"; }
    if (draft.Direction.Value == null || draft.Direction.Value == "") { return "入出金なし"; }
    if (draft.Amount.Value == null || draft.Amount.Value <= 0) { return "金額なし"; }
    if ($"{draft.CounterAccount.Value}" == $"{draft.CashAccount.Value}") { return "相手科目が現預金科目と同じ"; }
    if (IsProfitLossAccount(draft.CounterAccount.Value) && draft.DepartmentRef.Value == null) { return "部門なし"; }

    // 期間解決は月初日で行う（境界日知見: 月末日は辞書順比較で失敗する）
    var d = draft.EntryDate.Value;
    var monthFirst = new DateOnly(d.Year, d.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var year = ys.ExecuteFirstOrDefault();
    if (year == null) { return "会計年度なし"; }
    // **年度の締めも見る**（BUG-0100）。期間だけを見ていると、年次決算を終えて年度を締めたあとに
    // 1 か月だけ期間を再オープンした隙に、その年度へ確定仕訳を作れてしまう。
    // すると翌期の期首残高は古いまま残り、貸借が翌期にずれ込む。
    // 締めガードの粒度は `FixedAsset.GenerateDep_OnClick` の先例（年度 closed を明示的に止める）に揃える
    if (((FiscalYear)year).Status.Value == "closed") { return "会計年度が締め済み"; }
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { return "月次期間なし"; }
    if (((FiscalPeriod)period).Status.Value == "closed") { return "期間が締め済み"; }
    return "";
}

// 下書き 1 行を仕訳 1 本にする。戻り値は伝票番号（0 なら失敗）。消費税額は lastPostedTax に返す
int PostOne(CashEntryDraft draft)
{
    var entryDate = draft.EntryDate.Value;
    var monthFirst = new DateOnly(entryDate.Year, entryDate.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { return 0; }
    var typedFy = (FiscalYear)fy;

    // 採番は 1 本ずつ取り直す（同じ年度に連続で起票するため、前の 1 本を含めた最大値が要る）。
    // 正典: JournalEntry.NextJournalNo（BUG-0069 で一本化）
    var nextNo = new JournalEntry().NextJournalNo(typedFy.Id.Value);

    int amount = draft.Amount.Value;
    var isIn = (draft.Direction.Value == "in");
    var desc = draft.Description.Value;
    if (desc == null || desc == "") { desc = isIn ? "入金" : "出金"; }

    var je = new JournalEntry();
    je.EntryDate.Value = entryDate;
    je.EntryType.Value = "auto";
    je.Description.Value = desc;
    je.Status.Value = "posted";
    je.JournalNo.Value = nextNo;
    je.FiscalYearRef.Value = typedFy.Id.Value;
    je.SourceType.Value = "cashbook";
    je.Lines.AddRows(2);
    var idx = 0;
    foreach (var row in je.Lines.Rows)
    {
        var l = (JournalLine)row;
        idx = idx + 1;
        l.LineNo.Value = idx;
        l.Description.Value = desc;
        l.TaxInputMode.Value = "none";
        l.Amount.Value = amount;
        l.InputAmount.Value = amount;
        if (idx == 1)
        {
            l.Dc.Value = "D";
            l.Account.Value = isIn ? draft.CashAccount.Value : draft.CounterAccount.Value;
        }
        else
        {
            l.Dc.Value = "C";
            l.Account.Value = isIn ? draft.CounterAccount.Value : draft.CashAccount.Value;
        }
    }

    // 相手科目そのものが取引の経済的実体なので、勘定科目マスタの既定税区分を明示的に入れる
    // （現預金側は対象外のまま）。一律に対象外へ倒すと受取利息のような非課税売上を取りこぼし、
    // 課税売上割合の分母が狂う（ADR-0052）。相手科目が課税区分なら**内税**として扱い、
    // 確定前に消費税行を生成する（ADR-0053）。外税は使わない——外税だと本体行が税込のまま
    // 税行が増えて貸借が崩れる。
    // 部門は損益科目の行にだけ入れる（BS 側の現預金行には入れない。ADR-0056）
    var counterS = new ModuleSearcher<Account>();
    counterS.AddEquals(e => e.Id.Value, draft.CounterAccount.Value);
    var counterAcc = counterS.ExecuteFirstOrDefault();
    var isTaxable = false;
    if (counterAcc != null)
    {
        var typedCounter = (Account)counterAcc;
        var counterTaxCat = typedCounter.DefaultTaxCategory.Value;
        if (counterTaxCat != null)
        {
            var tcS = new ModuleSearcher<TaxCategory>();
            tcS.AddEquals(e => e.Id.Value, counterTaxCat);
            var tcm = tcS.ExecuteFirstOrDefault();
            if (tcm != null)
            {
                var taxType = ((TaxCategory)tcm).TaxationType.Value;
                if (taxType == "taxable_sales" || taxType == "taxable_purchase") { isTaxable = true; }
            }
        }
        var isPl = (typedCounter.AccountType.Value == "expense" || typedCounter.AccountType.Value == "revenue");
        foreach (var row in je.Lines.Rows)
        {
            var l = (JournalLine)row;
            if ($"{l.Account.Value}" != $"{draft.CounterAccount.Value}") continue;
            l.TaxCategory.Value = counterTaxCat;
            if (isTaxable) { l.TaxInputMode.Value = "inclusive"; }
            if (isPl) { l.Department.Value = draft.DepartmentRef.Value; }
        }
    }

    // **現預金行の税区分を明示的に「対象外」にする**（BUG-0105・ADR-0053 の教訓）。
    // `l.Account.Value` を入れると `Lines_OnDataChanged → ApplyLineDefaults()` が発火し、
    // **科目マスタの既定税区分が勝手に入る**。「セットしない」は「既定が入らない」ではない。
    // `MarkRemainingLinesOutOfScope()` は `TaxCategory == null` の行しか埋めないので、
    // 既定が入った現預金行は素通りしてしまう
    var oos = new ModuleSearcher<TaxCategory>();
    oos.AddEquals(e => e.TaxationType.Value, "out_of_scope");
    var oosCat = oos.ExecuteFirstOrDefault();
    if (oosCat != null)
    {
        foreach (var row in je.Lines.Rows)
        {
            var l = (JournalLine)row;
            if (l.IsTaxLine.Value == true) continue;
            if ($"{l.Account.Value}" == $"{draft.CounterAccount.Value}") continue;
            l.TaxCategory.Value = ((TaxCategory)oosCat).Id.Value;
            l.TaxInputMode.Value = "none";
        }
    }

    je.MarkRemainingLinesOutOfScope();

    // 税行の生成は入力額（税込）のまま 1 回だけ。この経路は下書きを経ずに確定するので順路は 1 本。
    // 税額は Submit の前に素の int に取り出しておく（保存後に動的値へ書式指定を掛けると空になる）。
    var taxAmount = 0;
    if (isTaxable)
    {
        je.GenerateTaxLinesOnce();
        foreach (var row in je.Lines.Rows)
        {
            var l = (JournalLine)row;
            if (l.IsTaxLine.Value == true) { taxAmount = l.Amount.Value ?? 0; }
        }
    }

    je.FillMissingDepartments();  // 部門は NOT NULL。空の行を全社共通で埋める（ADR-0056）
    // 貸借一致の検証（BUG-0068）。**Submit の前**に見るので、止めれば伝票は生まれない
    var imbalance = je.ValidateBalanced();
    if (imbalance != "")
    {
        Toaster.Error($"入出金の起票を中止しました（{imbalance}）");
        return 0;
    }
    var ret = je.Submit();
    if (ret != true) { return 0; }
    lastPostedTax = taxAmount;
    return nextNo;
}

// ============ 残高と件数の表示 ============

void DraftList_OnDataChanged()
{
    RefreshSummary();
}

// 帳簿残高（確定仕訳のみ・CashBalance クエリ）に、自分の未起票下書きを足した見込みを併記する。
// 「常に正しい今の残高」は他人が同時に起票すれば変わるので作れない。基準時刻を添えて
// 「いつ時点か」を明示する（市販ソフトの出納帳残高も画面を開いた時点のスナップショット）。
void RefreshSummary()
{
    BalanceList.Reload();

    var deltaByAccount = new Dictionary<string, int>();
    var inTotal = 0;
    var outTotal = 0;
    var count = 0;
    foreach (var row in DraftList.Rows)
    {
        var t = (CashEntryDraft)row;
        count = count + 1;
        var amount = t.Amount.Value ?? 0;
        var isIn = (t.Direction.Value == "in");
        if (isIn) { inTotal = inTotal + amount; } else { outTotal = outTotal + amount; }
        var key = $"{t.CashAccount.Value}";
        var signed = isIn ? amount : -amount;
        if (deltaByAccount.ContainsKey(key)) { deltaByAccount[key] = deltaByAccount[key] + signed; }
        else { deltaByAccount.Add(key, signed); }
    }

    foreach (var row in BalanceList.Rows)
    {
        var b = (CashBalance)row;
        var key = $"{b.AccountId.Value}";
        var delta = 0;
        if (deltaByAccount.ContainsKey(key)) { delta = deltaByAccount[key]; }
        b.DraftDelta.Value = delta;
        b.ExpectedBalance.Value = (b.BookBalance.Value ?? 0) + delta;
    }

    BalanceNoteLabel.Text = $"現預金の残高（{DateTime.Now:yyyy/MM/dd HH:mm} 現在）。「起票後の見込み」は下の下書きを全部起票したときの残高です。";
    if (count == 0)
    {
        DraftNoteLabel.Text = "未起票の下書きはありません。上の欄に入れて「明細に追加」で積んでいきます。";
    }
    else
    {
        DraftNoteLabel.Text = $"未起票の下書き {count} 件（入金 {inTotal:#,0} 円 ／ 出金 {outTotal:#,0} 円）。表の中で直せます（金額・科目・部門・摘要）。直したら「変更を保存」。";
    }
}
