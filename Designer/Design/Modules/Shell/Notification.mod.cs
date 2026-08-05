// Notification.mod.cs — 全社通知基盤（基盤・レイヤ0。ADR-0045 で経費部品から昇格）
// 送信は Send() に一元化（全部品から new Notification().Send(...) で呼ぶ＝部品→基盤の下方向参照）。
// リンクは論理（LinkModule + LinkId）で保存し、閲覧時に受信者のその時点の権限でフレーム解決する。
// 「開く」: 既読化してリンク先（申請等）へ遷移する。リンクが無ければ既読化のみ。
// ListField 行モジュールのため、値は Id で DB から取り直す（B2-1 OpenRequest と同じ規律。
// レイアウト外フィールドの .Value は信用しない）

// ============================================================
// 送信 API（全部品の唯一の通知送信窓口）
// 他人宛 INSERT は DataReadCondition（自分宛のみ）により Submit 後の再読込が 0 件になり、
// INSERT 自体は成功していても true 以外が返る（2026-07-08 実測）。そのため戻り値では成否判定せず
// ログのみ残す。Slack/メール連携（現状 mock）の実装差し込み点もここ。
void Send(object recipientUserId, string title, string body, string linkModule, string linkId)
{
    if (recipientUserId == null) return;
    var n = new Notification();
    n.RecipientUser.Value = recipientUserId;
    n.Title.Value = title;
    n.Body.Value = body;
    n.LinkModule.Value = linkModule;
    n.LinkId.Value = linkId;
    n.IsRead.Value = false;
    n.CreatedAt.Value = DateTime.Now;
    var ret = n.Submit();
    if (ret != true) { Logger.Log($"通知 Submit の戻り値が true 以外（他ユーザー宛では正常挙動）: {title}"); }
    Logger.Log($"SLACK(mock): to user#{recipientUserId} {title} - {body}");
}

// 論理リンク → URL の解決（閲覧時・受信者の権限基準。ADR-0045）
// 通知一覧はポータル（Main フレーム）に置くため、遷移先は部品フレームを明示する必要がある。
// 新しい部品の通知を追加したらここに分岐を足す（モジュール名の文字列マッピング＝型参照ではない）
string ResolveLinkUrl(string linkModule, string linkId)
{
    if (linkModule == "ExpenseRequest")
    {
        var frame = "ExpenseStaff";
        if (CurrentUser.HasAccountingAccess.Value == true) { frame = "ExpenseAccounting"; }
        else if (CurrentUser.IsApprover.Value == true) { frame = "ExpenseApprover"; }
        return $"/{frame}/ExpenseRequest/{linkId}";
    }
    return null;
}

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
        var url = ResolveLinkUrl(linkModule, $"{linkId}");
        if (url != null)
        {
            NavigationService.NavigateTo(url);
        }
        else
        {
            // 未知のリンク先: 現在フレーム解決にフォールバック（登録が無ければ CLB がエラートーストを出す）
            NavigationService.NavigateTo(NavigationService.GetModuleDataUrl(linkModule, linkId));
        }
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
