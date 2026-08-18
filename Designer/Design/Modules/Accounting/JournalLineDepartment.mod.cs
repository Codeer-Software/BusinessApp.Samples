// JournalLineDepartment.mod.cs — 部門・プロジェクトの修正（振替伝票のサブ画面・経理専用）
// 責務: **確定済みの伝票でも部門とプロジェクトだけは後から直せるようにする**（ADR-0056 決定 4）。
//
// なぜ許されるか: 部門とプロジェクトは仕訳の構成要素ではなく集計のための分類で、変えても
// BS・PL・試算表・消費税集計表は 1 円も動かない。動くのは部門別 P/L・案件損益・予実対比だけ。
// 逆に摘要は法定帳簿の記載事項なので、ここでは触らせない（訂正は赤黒で）。
//
// なぜ別モジュールなのか: 確定済み伝票は `this.IsViewOnly = true` で丸ごとロックしており、
// 明細グリッドの「部門の列だけ編集可」は**レイアウト（設計時）でしか指定できない**ため、
// 同じグリッドで「下書きは全項目編集／確定済みは部門だけ」を切り替えられない（実測 2026-08-14）。
//
// ただし**利用者から見て別画面に飛んだ感じにはしない**（2026-08-14 ユーザー指示）。
//   ・入口は振替伝票詳細の「部門・プロジェクトを修正する」ボタンだけ（サイドメニューには出さない）
//   ・見た目は振替伝票の詳細とほぼ同じ（ヘッダの並び・明細の列順を合わせ、直せない列は編集不可で見せる）
//   ・保存すると元の伝票詳細へ戻る
//   ・戻るは**左上の矢印**（振替伝票と同じ BackAnchor）。「保存せずに戻る」ボタンは置かない——
//     何も壊さない操作に赤（Danger）を使うと、本当に危ない赤が効かなくなる（ADR-0027）
// 対象の伝票はクエリパラメータ `?entry={伝票Id}` で受け取る。

void Detail_OnAfterInit()
{
    ResultLabel.Text = "";
    ClosedNote.Text = "";
    LineList.IsVisible = false;
    SaveButton.IsVisible = false;

    // 入口は振替伝票のボタンだけ。URL を直に叩かれた場合はここで止まる
    var entryId = QueryEntryId();
    if (entryId == null)
    {
        DescNoteLabel.Text = "対象の伝票が指定されていません。振替伝票を開いて「部門・プロジェクトを修正する」から入ってください（左上の矢印で戻れます）。";
        return;
    }

    TargetEntryId.Value = entryId;
    var je = FindEntry();
    if (je == null)
    {
        DescNoteLabel.Text = "対象の伝票が見つかりません。振替伝票の一覧から開き直してください（左上の矢印で戻れます）。";
        return;
    }

    ShowEntry(je);
}

// 伝票ヘッダを振替伝票と同じ並びで見せ、明細を読み込む
void ShowEntry(JournalEntry je)
{
    JournalNoValue.Text = $"{je.JournalNo.Value}";
    EntryDateValue.Text = $"{je.EntryDate.Value:yyyy/MM/dd}";
    EntryTypeValue.Text = EntryTypeName(je.EntryType.Value);
    DescriptionValue.Text = $"{je.Description.Value}";

    LineList.Reload();
    LineList.IsVisible = true;

    var debit = 0;
    var credit = 0;
    foreach (var row in LineList.Rows)
    {
        var l = (JournalLine)row;
        var amount = l.Amount.Value ?? 0;
        if (l.Dc.Value == "D") { debit = debit + amount; } else { credit = credit + amount; }
    }
    DebitTotalValue.Text = $"{debit:#,0}";
    CreditTotalValue.Text = $"{credit:#,0}";

    // 締め済み期間はロックする。月次締めは「部門別 P/L を確定して配った」という意味を持つので、
    // 締めたあとに部門が変わると配布済みの報告と食い違う（他の統制と同じ規律）
    if (IsPeriodClosed(je))
    {
        ClosedNote.Text = "この伝票の期間は締め済みです。部門・プロジェクトも直せません（直すなら会計年度の設定で期間を再オープンしてください）。";
        SaveButton.IsVisible = false;
    }
    else
    {
        SaveButton.IsVisible = true;
    }
}

// ============ 保存して伝票に戻る ============

void Save_OnClick()
{
    if (!IsAccounting()) { return; }
    if (TargetEntryId.Value == null) { Toaster.Error("対象の伝票がありません"); return; }

    var je = FindEntry();
    if (je == null) { Toaster.Error("対象の伝票が見つかりません"); return; }
    if (IsPeriodClosed(je))
    {
        Toaster.Error("この伝票の期間は締め済みです。直すなら期間を再オープンしてください");
        return;
    }

    using var loading = LoadingService.StartLoading(0);

    // **失敗を握り潰さない**（BUG-0072）。`Submit()` が false を返すのは実際に起きる——
    // 画面の部門は空候補を選べるのに DB は `department_id NOT NULL` なので、
    // 空に戻した行の保存は必ず失敗する。旧実装は false を数えずに常に成功トーストを出しており、
    // **保存されたと信じて離脱する**という一番まずい壊れ方をしていた。
    // なお `Submit()` が null を返すのは「変更が無い」＝失敗ではない（この repo の既知の作法）。
    // 空の部門は DB が受け付けない（`department_id NOT NULL`）ので、**押す前に止めて場所を教える**
    var emptyLines = new List<string>();
    foreach (var row in LineList.Rows)
    {
        var l0 = (JournalLine)row;
        if (l0.Department.Value == null) { emptyLines.Add($"{l0.LineNo.Value}"); }
    }
    if (emptyLines.Count > 0)
    {
        Toaster.Error($"明細 {string.Join("・", emptyLines)} 行目の部門が空です。"
            + "部門は必須なので、選んでから保存してください（共通費なら「全社共通」）");
        return;
    }

    var saved = 0;
    var failed = 0;
    var failedLines = new List<string>();
    foreach (var row in LineList.Rows)
    {
        var l = (JournalLine)row;
        var ok = l.Submit();
        if (ok == true) { saved = saved + 1; }
        else if (ok == false)
        {
            failed = failed + 1;
            failedLines.Add($"{l.LineNo.Value}");
        }
    }

    if (failed > 0)
    {
        // 画面に留まる（BackToEntry しない）。直して押し直せるようにする
        Toaster.Error($"{failed} 行を保存できませんでした（明細 {string.Join("・", failedLines)} 行目）。"
            + "部門が空になっていないか確認してください（部門は必須です）");
        return;
    }

    // 伝票ヘッダの更新者・更新日時を動かす（ADR-0056 決定 4 の監査証跡）。
    // **明細だけ保存しても親行は UPDATE されない**ので、ここで明示的に触る。
    // CLB の予約名 UpdatedAt / Updater は保存時に自動セットされるが、
    // 「値が何も変わらない Submit」で UPDATE が走る保証が無いため、値を入れてから保存する
    je.UpdatedAt.Value = DateTime.Now;
    je.Updater.Value = CurrentUser.Id.Value;
    je.Submit();

    Toaster.Success($"部門・プロジェクトを保存しました（{saved} 行）");
    BackToEntry();
}

// 元の伝票詳細へ戻る（対象が分からないときは振替伝票の一覧へ）
void BackToEntry()
{
    if (TargetEntryId.Value == null)
    {
        // 戻り先は **3 フレームすべてに登録されている `JournalEntry`** にする（BUG-0071）。
        // `JournalEntryBoard` は会計フレームの着地画面で、購買・営業フレームには登録が無い。
        // そちらへ飛ばすと URL だけ変わって**中身が空**になる（CLB-013 の典型症状）。
        NavigationService.NavigateTo(NavigationService.GetModuleUrl("JournalEntry"));
        return;
    }
    NavigationService.NavigateTo(NavigationService.GetModuleDataUrl("JournalEntry", $"{TargetEntryId.Value}"));
}

// ============ 補助 ============

bool IsAccounting()
{
    if (CurrentUser.HasAccountingAccess.Value == true) { return true; }
    Toaster.Error("仕訳の修正は経理のみ実行できます");
    return false;
}

// クエリパラメータ ?entry={伝票Id} を読む（表示専用モジュールなので URL から受け取る）
object QueryEntryId()
{
    var q = NavigationService.GetUniqueQueryParameters();
    if (q == null) { return null; }
    if (!q.ContainsKey("entry")) { return null; }
    var raw = q["entry"];
    if (raw == null || raw == "") { return null; }
    var id = 0;
    if (!int.TryParse(raw, out id)) { return null; }
    if (id <= 0) { return null; }
    return id;
}

JournalEntry FindEntry()
{
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.Id.Value, TargetEntryId.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) { return null; }
    return (JournalEntry)found;
}

string EntryTypeName(string code)
{
    if (code == "transfer") { return "振替"; }
    if (code == "auto") { return "自動"; }
    if (code == "adjust") { return "決算整理"; }
    if (code == "receipt") { return "入金"; }
    if (code == "payment") { return "支払"; }
    if (code == "expense") { return "経費"; }
    return $"{code}";
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
