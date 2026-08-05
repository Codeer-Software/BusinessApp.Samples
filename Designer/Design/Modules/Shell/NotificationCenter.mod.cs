// NotificationCenter.mod.cs — 通知一覧のカスタム一覧（2026-08-06 レビュー第9弾）
// 標準の一覧ページには任意ボタンを置けない（#44）ため、検索＋リスト＋
// 「既読の通知を全て削除」ボタンを表示専用モジュールとして自前構成する（ReceiptBoard と同パターン）。
// 削除対象は Notification の DataReadCondition（自分宛のみ）により構造的に自分の通知に限られる。
// Notification は子リストを持たないため検索インスタンスの Delete() が使える
//（子持ちモジュールでは静かに失敗する既知の罠——2026-07-19 実測——に該当しない）。

void Detail_OnAfterInit()
{
    // 表示専用モジュールの Detail はビュー専用扱いになりボタンが押せなくなる（実測）。明示解除する
    IsViewOnly = false;
}

void ClearRead_OnClick()
{
    var s = new ModuleSearcher<Notification>();
    s.AddEquals(n => n.IsRead.Value, true);
    s.Limit(500);
    var rows = s.Execute();
    if (rows.Count == 0)
    {
        Toaster.Info("既読の通知はありません");
        return;
    }

    var answer = MessageBox.Show($"既読の通知 {rows.Count} 件をすべて削除します（元に戻せません）。よろしいですか？", "削除する", "キャンセル");
    if (answer != "削除する") return;

    using var loading = LoadingService.StartLoading(0);
    var failed = 0;
    foreach (var r in rows)
    {
        var n = (Notification)r;
        var ret = n.Delete();
        if (ret != true)
        {
            failed = failed + 1;
        }
    }
    Results.Reload();
    if (failed > 0)
    {
        Toaster.Error($"{failed} 件の削除に失敗しました（残りは削除済み）");
    }
    else
    {
        Toaster.Success($"既読の通知 {rows.Count} 件を削除しました");
    }
}
