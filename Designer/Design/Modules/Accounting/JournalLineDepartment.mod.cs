// JournalLineDepartment.mod.cs — 仕訳の部門・案件を直す（表示専用モジュール・経理専用）
// 責務: **確定済みの伝票でも部門と案件だけは後から直せるようにする**（ADR-0056 決定 4）。
//
// なぜ許されるか: 部門と案件は仕訳の構成要素ではなく分析の軸で、変えても BS・PL・試算表・
// 消費税集計表は 1 円も動かない。動くのは部門別 P/L・案件損益・予実対比だけ。
// 逆に摘要は法定帳簿の記載事項なので、ここでは触らせない（訂正は赤黒で）。
//
// なぜ専用画面か: 確定済み伝票は `this.IsViewOnly = true` で丸ごとロックしており、
// 明細グリッドの「部門の列だけ編集可」は**レイアウト（設計時）でしか指定できない**ため、
// 同じグリッドで「下書きは全項目編集／確定済みは部門だけ」を切り替えられない（実測 2026-08-14）。
// 部門だけを編集可にした専用のリストレイアウト（JournalLine の "DeptEdit"）を別画面で使う。

void Detail_OnAfterInit()
{
    EntryInfoLabel.Text = "";
    ResultLabel.Text = "";
    TargetEntryId.Value = null;
    SaveButton.IsVisible = false;
    LineList.IsVisible = false;
}

// ============ 伝票番号 → 明細を出す ============

void Show_OnClick()
{
    if (!IsAccounting()) { return; }
    if (JournalNoInput.Value == null)
    {
        Toaster.Error("伝票番号を入力してください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);

    var je = FindEntry();
    if (je == null)
    {
        EntryInfoLabel.Text = "";
        LineList.IsVisible = false;
        SaveButton.IsVisible = false;
        TargetEntryId.Value = null;
        Toaster.Error($"伝票 No.{JournalNoInput.Value} が見つかりません（当年度の伝票番号を入れてください）");
        return;
    }

    TargetEntryId.Value = je.Id.Value;
    LineList.Reload();
    LineList.IsVisible = true;

    var statusText = (je.Status.Value == "posted") ? "確定" : "下書き";
    var closedNote = "";
    if (IsPeriodClosed(je))
    {
        closedNote = " ／ この伝票の期間は締め済みです（部門・案件も直せません。直すなら期間を再オープンしてください）";
        SaveButton.IsVisible = false;
    }
    else
    {
        SaveButton.IsVisible = true;
    }
    EntryInfoLabel.Text = $"伝票 No.{je.JournalNo.Value} ／ {je.EntryDate.Value:yyyy/MM/dd} ／ {je.Description.Value} ／ 状態 {statusText}{closedNote}";
    ResultLabel.Text = "";
}

// ============ 保存 ============

void Save_OnClick()
{
    if (!IsAccounting()) { return; }
    if (TargetEntryId.Value == null) { Toaster.Error("先に伝票を表示してください"); return; }

    var je = FindEntry();
    if (je == null) { Toaster.Error("伝票が見つかりません"); return; }

    // 締め済み期間はロックする。月次締めは「部門別 P/L を確定して配った」という意味を持つので、
    // 締めたあとに部門が変わると配布済みの報告と食い違う（他の統制と同じ規律）
    if (IsPeriodClosed(je))
    {
        Toaster.Error("この伝票の期間は締め済みです。直すなら期間を再オープンしてください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);

    var saved = 0;
    foreach (var row in LineList.Rows)
    {
        var l = (JournalLine)row;
        if (l.Submit() == true) { saved = saved + 1; }
    }

    // 伝票ヘッダの更新者・更新日時を動かす（ADR-0056 決定 4 の監査証跡）。
    // **明細だけ保存しても親行は UPDATE されない**ので、ここで明示的に触る。
    // CLB の予約名 UpdatedAt / Updater は保存時に自動セットされるが、
    // 「値が何も変わらない Submit」で UPDATE が走る保証が無いため、値を入れてから保存する
    je.UpdatedAt.Value = DateTime.Now;
    je.Updater.Value = CurrentUser.Id.Value;
    je.Submit();

    LineList.Reload();
    ResultLabel.Text = $"伝票 No.{je.JournalNo.Value} の明細 {saved} 行を保存しました（{DateTime.Now:yyyy/MM/dd HH:mm}）";
    Toaster.Success($"部門・案件を保存しました（{saved} 行）");
}

// ============ 補助 ============

bool IsAccounting()
{
    if (CurrentUser.HasAccountingAccess.Value == true) { return true; }
    Toaster.Error("仕訳の修正は経理のみ実行できます");
    return false;
}

// 当年度の伝票番号で伝票を引く（伝票番号は年度内連番なので、年度で絞らないと重複しうる）
JournalEntry FindEntry()
{
    var today = DateOnly.FromDateTime(DateTime.Today);
    var monthFirst = new DateOnly(today.Year, today.Month, 1);
    var ys = new ModuleSearcher<FiscalYear>();
    ys.AddLessThanOrEqual(e => e.StartDate.Value, monthFirst);
    ys.AddGreaterThanOrEqual(e => e.EndDate.Value, monthFirst);
    var fy = ys.ExecuteFirstOrDefault();
    if (fy == null) { return null; }

    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.FiscalYearRef.Value, ((FiscalYear)fy).Id.Value);
    s.AddEquals(e => e.JournalNo.Value, JournalNoInput.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) { return null; }
    return (JournalEntry)found;
}

bool IsPeriodClosed(JournalEntry je)
{
    if (je.EntryDate.Value == null) { return false; }
    var d = je.EntryDate.Value;
    var firstDay = new DateOnly(d.Year, d.Month, 1);
    var ps = new ModuleSearcher<FiscalPeriod>();
    ps.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
    ps.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
    var period = ps.ExecuteFirstOrDefault();
    if (period == null) { return false; }
    return ((FiscalPeriod)period).Status.Value == "closed";
}
