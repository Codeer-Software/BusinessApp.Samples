-- 680_inbox_submitted_at.sql — 承認待ちの「申請日時」を提出時刻にする（BUG-0305・開発者判断 2026-08-18）
--
-- 【問題】受信箱の「申請日時」が `approval_flow.created_at`＝**下書きを作った時刻**だった。
-- 承認者にとっての「いつ申請されたか」は**提出した時刻**なので、実測で 24 秒ずれていた
-- （一覧 9:31:22 ／ 履歴の「申請」9:31:46）。再申請した申請では差はもっと大きくなる。
--
-- 【方式】承認履歴の **最新の Submit / Resubmit** の時刻を採る。再申請したら「最後に出した時刻」で
-- 並ぶのが承認者の期待に合う（古い初回提出時刻のまま埋もれない）。
-- 履歴が無い異常データでは従来どおり `approval_flow.created_at` に落とす。
--
-- 列は既存の `created_at` をそのまま置き換える（モジュール側の変更が要らない）。
-- 640/650 と同じく再実行可（DROP → CREATE）。
DROP VIEW IF EXISTS approval_inbox_view;

CREATE VIEW approval_inbox_view AS
SELECT m.id                 AS id,
       f.parent_module_name AS parent_module_name,
       f.parent_id          AS parent_id,
       f.status             AS status,
       m.approver_user_id   AS current_approver,
       f.creator            AS creator,
       COALESCE((SELECT MAX(h.acted_at) FROM approval_history h
                 WHERE h.approval_flow_id = f.id AND h.action IN ('Submit', 'Resubmit')),
                f.created_at) AS created_at,
       CASE WHEN f.parent_module_name = 'ExpenseRequest' THEN er.title  END AS title,
       CASE WHEN f.parent_module_name = 'ExpenseRequest' THEN er.amount END AS amount
FROM approval_flow_member m
JOIN approval_flow_order  o ON o.id = m.approval_flow_order_id
JOIN approval_flow        f ON f.id = o.approval_flow_id
LEFT JOIN expense_request er
  ON f.parent_module_name = 'ExpenseRequest'
 AND er.id = CAST(f.parent_id AS INTEGER)
WHERE m.status = 'Waiting' AND o.status = 'Active' AND f.status = 'Pending';
