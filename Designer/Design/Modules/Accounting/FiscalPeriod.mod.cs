// FiscalPeriod.mod.cs — 月次期間
// 磨きバックログ: 期間を「締め済み」に変更したとき、その期間に下書き（draft）伝票が
// 残っていれば警告する（ブロックはしない。締め自体は保存で確定する運用のまま）

void Status_OnDataChanged()
{
    if (Status.Value != "closed") return;
    if (StartDate.Value == null || EndDate.Value == null) return;

    var s = new ModuleSearcher<JournalEntry>();
    s.AddEquals(e => e.Status.Value, "draft");
    s.AddGreaterThanOrEqual(e => e.EntryDate.Value, StartDate.Value);
    s.AddLessThanOrEqual(e => e.EntryDate.Value, EndDate.Value);
    var drafts = s.Execute();
    if (drafts.Count > 0)
    {
        Toaster.Warn($"この期間には下書きの伝票が {drafts.Count} 件残っています。締める前に確定または削除を検討してください（保存すると締めは実行されます）");
    }
}
