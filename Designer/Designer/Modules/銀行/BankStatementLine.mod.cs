// BankStatementLine.mod.cs — 明細一覧の詳細画面（v3 新設。ISSUE-0003）
// 一覧は全状態の通帳ビュー（表示専用・削除なし）。例外操作は本詳細画面に集約:
//   pending    → 「対象外にする」「この明細を削除」
//   excluded   → 「未起票に戻す」「この明細を削除」
//   journalized→ 「起票を取り消す」（紐づく仕訳ごと削除。締め済み期間は拒否＝赤伝誘導）
// 単発操作は確認ダイアログ＋即 DB 反映（一覧の一括編集と規律を分ける。ISSUE-0003 §2.4）

void Detail_OnAfterInit()
{
    UpdateButtons();
}

// 相手科目候補の手修正時に推定元を manual にする（一括起票のリスト編集用。
// ルール/AI がスクリプトから設定する場合は、この後で rule/ai を上書きするので影響しない）
void SuggestedAccount_OnDataChanged()
{
    if (SuggestedAccount.Value == null) { SuggestionSource.Value = null; }
    else { SuggestionSource.Value = "manual"; }
}

void UpdateButtons()
{
    // 詳細は表示専用。ボタンは CLB 1.3 の仕様（モジュール ViewOnly はボタンクリックも抑止）
    // に対応するため、個別に IsViewOnly=false を明示する（FB-030）
    this.IsViewOnly = true;
    ExcludeButton.IsViewOnly = false;
    RestoreButton.IsViewOnly = false;
    UnpostButton.IsViewOnly = false;
    DeleteLineButton.IsViewOnly = false;

    var st = Status.Value;
    ExcludeButton.IsVisible = st == "pending";
    RestoreButton.IsVisible = st == "excluded";
    UnpostButton.IsVisible = st == "journalized";
    DeleteLineButton.IsVisible = st == "pending" || st == "excluded";

    if (st == "pending")
    {
        NoteLabel.Text = "未起票の明細です。相手科目の確定と起票は「一括起票」画面で行います";
    }
    else if (st == "excluded")
    {
        NoteLabel.Text = "対象外の明細です（起票されません）。誤って対象外にした場合は「未起票に戻す」で起票対象に戻せます";
    }
    else if (st == "journalized")
    {
        var no = FindJournalNo();
        if (no != null) { NoteLabel.Text = $"起票済の明細です（仕訳 伝票 No.{no}）。仕訳ごと取り消す場合のみ「起票を取り消す」を使います"; }
        else { NoteLabel.Text = "起票済の明細です（紐づく仕訳が見つかりません。データを確認してください）"; }
    }
    else
    {
        NoteLabel.Text = "";
    }
}

// 紐づく仕訳（無ければ null）
JournalEntry FindJournalEntry()
{
    if (JournalEntryId.Value == null) return null;
    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.Id.Value, JournalEntryId.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return null;
    return (JournalEntry)found;
}

object FindJournalNo()
{
    var je = FindJournalEntry();
    if (je == null) return null;
    return je.JournalNo.Value;
}

// ============ pending → excluded ============

void Exclude_OnClick()
{
    if (Status.Value != "pending") { Toaster.Error("未起票の明細のみ対象外にできます"); return; }
    var answer = MessageBox.Show("この明細を対象外にします（起票されなくなります）。よろしいですか？", "対象外にする", "キャンセル");
    if (answer != "対象外にする") return;

    this.IsViewOnly = false;
    Status.Value = "excluded";
    var ok = this.Submit();
    if (ok != true) { Toaster.Error("状態の更新に失敗しました"); UpdateButtons(); return; }
    UpdateButtons();
    Toaster.Success("明細を対象外にしました");
}

// ============ excluded → pending ============

void Restore_OnClick()
{
    if (Status.Value != "excluded") { Toaster.Error("対象外の明細のみ未起票に戻せます"); return; }
    var answer = MessageBox.Show("この明細を未起票に戻します（一括起票の対象に戻ります）。よろしいですか？", "未起票に戻す", "キャンセル");
    if (answer != "未起票に戻す") return;

    this.IsViewOnly = false;
    Status.Value = "pending";
    var ok = this.Submit();
    if (ok != true) { Toaster.Error("状態の更新に失敗しました"); UpdateButtons(); return; }
    UpdateButtons();
    Toaster.Success("明細を未起票に戻しました");
}

// ============ journalized → pending（起票の取り消し。仕訳ごと削除） ============

void Unpost_OnClick()
{
    if (Status.Value != "journalized") { Toaster.Error("起票済の明細のみ取り消せます"); return; }

    var je = FindJournalEntry();
    if (je == null)
    {
        // リンク切れ（仕訳が手動削除された等）: 状態だけ戻して整合させる
        var a = MessageBox.Show("紐づく仕訳が見つかりませんでした。明細の状態だけを未起票に戻します。よろしいですか？", "未起票に戻す", "キャンセル");
        if (a != "未起票に戻す") return;
        this.IsViewOnly = false;
        JournalEntryId.Value = null;
        Status.Value = "pending";
        this.Submit();
        UpdateButtons();
        Toaster.Success("明細を未起票に戻しました");
        return;
    }

    // 締め済み期間ガード（月次締め済みの仕訳は削除不可＝赤伝で対応する。docs/tests/06 冒頭の規律）
    var d = je.EntryDate.Value;
    if (d != null)
    {
        var firstDay = new DateTime(d.Year, d.Month, 1);
        var ps = new ModuleSearcher<FiscalPeriod>();
        ps.AddLessThanOrEqual(e => e.StartDate.Value, firstDay);
        ps.AddGreaterThanOrEqual(e => e.EndDate.Value, firstDay);
        var period = ps.ExecuteFirstOrDefault();
        if (period != null && ((FiscalPeriod)period).Status.Value == "closed")
        {
            Toaster.Error($"仕訳 No.{je.JournalNo.Value} の期間は締め済みのため起票を取り消せません（赤伝（反対仕訳）で対応してください）");
            return;
        }
    }

    var answer = MessageBox.Show($"仕訳 伝票 No.{je.JournalNo.Value} を削除して、この明細を未起票に戻します。よろしいですか？", "取り消す", "キャンセル");
    if (answer != "取り消す") return;

    using var loading = LoadingService.StartLoading(0);

    // FK 解放のため先に明細側のリンクを外す（失敗時は復元する。Project.md 2026-07-19 知見）
    var jeId = JournalEntryId.Value;
    this.IsViewOnly = false;
    JournalEntryId.Value = null;
    Status.Value = "pending";
    var okLink = this.Submit();
    if (okLink != true) { Toaster.Error("明細の更新に失敗しました（仕訳は削除されていません）"); UpdateButtons(); return; }

    if (!DeleteJournalEntryWithLines(je))
    {
        // 仕訳削除に失敗: 明細側を起票済みに復元して整合を保つ
        this.IsViewOnly = false;
        JournalEntryId.Value = jeId;
        Status.Value = "journalized";
        this.Submit();
        UpdateButtons();
        Toaster.Error("仕訳の削除に失敗しました（明細は起票済みのままです）");
        return;
    }

    UpdateButtons();
    Toaster.Success($"仕訳 伝票 No.{je.JournalNo.Value} を削除し、明細を未起票に戻しました");
}

// 仕訳の削除: 明細行を1行ずつ削除してから親を削除する（検収・入金の取消と同じパターン）
bool DeleteJournalEntryWithLines(JournalEntry je)
{
    var ls = new ModuleSearcher<JournalLine>();
    ls.AddEquals(l => l.JournalEntryId.Value, je.Id.Value);
    var lines = ls.Execute();
    foreach (var row in lines)
    {
        var l = (JournalLine)row;
        var okLine = l.Delete();
        if (okLine != true) { return false; }
    }
    var ok = je.Delete();
    if (ok != true) { return false; }
    return true;
}

// ============ pending / excluded → 削除 ============

void DeleteLine_OnClick()
{
    var st = Status.Value;
    if (st != "pending" && st != "excluded") { Toaster.Error("未起票・対象外の明細のみ削除できます（起票済は先に起票を取り消してください）"); return; }
    var answer = MessageBox.Show("この明細を削除します。重複キーが解放されるため、同じ明細を再取込できるようになります。よろしいですか？", "削除する", "キャンセル");
    if (answer != "削除する") return;

    using var loading = LoadingService.StartLoading(0);
    var ok = this.Delete();
    if (ok != true) { Toaster.Error("明細の削除に失敗しました"); return; }
    Toaster.Success("明細を削除しました");
    NavigationService.NavigateTo(NavigationService.GetModuleUrl("BankStatementLine"));
}
