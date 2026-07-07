// Notification.mod.cs — 通知一覧
// 「開く」: 既読化してリンク先（申請等）へ遷移する。リンクが無ければ既読化のみ。
// ListField 行モジュールのため、値は Id で DB から取り直す（B2-1 OpenRequest と同じ規律。
// レイアウト外フィールドの .Value は信用しない）

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
