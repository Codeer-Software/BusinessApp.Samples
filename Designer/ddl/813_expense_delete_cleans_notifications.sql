-- 813_expense_delete_cleans_notifications.sql — 経費申請を削除したら通知も片付ける（BUG-0236）
--
-- `ddl/810` で承認フロー（段・メンバー・履歴）は連鎖削除するようにしたが、**通知が残っていた**。
-- 通知は削除済みレコードへのリンクを持つので、受信者が「開く」を押すと
-- **全項目が空の詳細画面**に飛ぶ（エラーも「ありません」も出ない）。
-- 2026-08-19 の実測: 開発2部 第二課長の受信箱にある未読 9 件のうち 4 件が
-- 削除済み申請へのリンクで、実際に空の承認画面が開いた。
--
-- 通知の `link_module` は `ExpenseRequest`（申請者向け）と `ExpenseRequestApproval`（承認者向け）の
-- 2 種類で、どちらも `link_id` は `expense_request.id`。両方を対象にする。
--
-- なぜ DB 側でやるか: `ExpenseRequest.DeleteDraft_OnClick` は子レコードをスクリプトから
-- 物理削除できない（810 のコメント参照）。削除の連鎖は DB の責務として一本化する。

DROP TRIGGER IF EXISTS trg_expense_request_delete_notifications;

CREATE TRIGGER trg_expense_request_delete_notifications
AFTER DELETE ON expense_request
FOR EACH ROW
BEGIN
  DELETE FROM notifications
   WHERE link_module IN ('ExpenseRequest', 'ExpenseRequestApproval')
     AND CAST(link_id AS INTEGER) = old.id;
END;

-- 既に残っている孤児を掃除する（2026-08-19 時点で 15 件）
DELETE FROM notifications
 WHERE link_module IN ('ExpenseRequest', 'ExpenseRequestApproval')
   AND NOT EXISTS (SELECT 1 FROM expense_request e WHERE e.id = CAST(notifications.link_id AS INTEGER));
