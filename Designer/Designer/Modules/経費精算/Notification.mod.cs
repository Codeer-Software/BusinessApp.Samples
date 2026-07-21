// Notification.mod.cs — 通知一覧
// 「開く」: 既読化してリンク先（申請等）へ遷移する。リンクが無ければ既読化のみ。
// ListField 行モジュールのため、値は Id で DB から取り直す（B2-1 OpenRequest と同じ規律。
// レイアウト外フィールドの .Value は信用しない）

// 補足: 行単位の「未読に戻す」ボタン出し分け（既読行のみ表示）は CLB では実現できない
// （ListLayout の OnAfterInitialization は行モジュールに配られず、フィールドの OnDataChanged も
//  ロード時には発火しない——2026-07-21 実測）。未読行でのクリックはガードで無害化し、
// 全行未読のホーム「未読の通知」はボタン無しの HomeUnread レイアウトを使う。

void Open_OnClick()
{
    var s = new ModuleSearcher<Notification>();
    s.AddEquals(n => n.Id.Value, Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return;
    var typed = (Notification)found;
    var linkModule = typed.LinkModule.Value;
    var linkId = typed.LinkId.Value;

    if (typed.IsRead.Value != true)
    {
        typed.IsRead.Value = true;
        var ret = typed.Submit();
        if (ret != true) { Logger.Warn("通知の既読化に失敗しました"); }
        IsRead.Value = true;
        MarkUnreadButton.IsVisible = true;
    }

    if (linkModule != null && linkModule != "" && linkId != null && linkId != "")
    {
        NavigationService.NavigateTo(NavigationService.GetModuleDataUrl(linkModule, linkId));
    }
    else
    {
        Toaster.Info("既読にしました");
    }
}

// 未読に戻す（誤既読のリカバリ。2026-07-21 ユーザー要望）
void MarkUnread_OnClick()
{
    var s = new ModuleSearcher<Notification>();
    s.AddEquals(n => n.Id.Value, Id.Value);
    var found = s.ExecuteFirstOrDefault();
    if (found == null) return;
    var typed = (Notification)found;
    if (typed.IsRead.Value != true)
    {
        Toaster.Info("この通知は未読です");
        return;
    }
    typed.IsRead.Value = false;
    var ret = typed.Submit();
    if (ret != true) { Toaster.Error("未読に戻せませんでした"); return; }
    IsRead.Value = false;
    MarkUnreadButton.IsVisible = false;
    Toaster.Success("通知を未読に戻しました");
}
