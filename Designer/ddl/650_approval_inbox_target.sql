-- 650_approval_inbox_target.sql — 承認待ち一覧に「件名・金額」を出す（BUG-0303・BusinessAppSQLite）
--
-- 【問題】受信箱の列が 申請種別 / 状態 / 申請者 / 申請日時 だけで、同じ人から 2 件届くと
--   1 件ずつ開かないと何をいくら承認するのか分からない。経理の「精算処理待ち」には件名・金額があり、
--   通知メールの本文にも「経費申請「件名」（金額円）」と入っているのに、承認者の一覧だけが情報を欠く。
--
-- 【方式】汎用承認（approval_flow）は件名・金額を持たないので、申請種別ごとの親伝票を LEFT JOIN する。
--   **`my_application_view`（ddl/440）と同じ形**にそろえた——申請者側と承認者側で違う作り方をすると、
--   申請種別が増えたときに片方だけ直す事故になる。申請種別が増えたら両方に LEFT JOIN と CASE を足す。
--
-- 列の互換: 既存 6 列（id / parent_module_name / parent_id / status / current_approver / creator /
--   created_at）はそのまま。title / amount を末尾に足すだけなので、モジュール側は列を足すだけでよい。
DROP VIEW IF EXISTS approval_inbox_view;

CREATE VIEW approval_inbox_view AS
SELECT m.id                 AS id,
       f.parent_module_name AS parent_module_name,
       f.parent_id          AS parent_id,
       f.status             AS status,
       m.approver_user_id   AS current_approver,
       f.creator            AS creator,
       f.created_at         AS created_at,
       CASE WHEN f.parent_module_name = 'ExpenseRequest' THEN er.title  END AS title,
       CASE WHEN f.parent_module_name = 'ExpenseRequest' THEN er.amount END AS amount
FROM approval_flow_member m
JOIN approval_flow_order  o ON o.id = m.approval_flow_order_id
JOIN approval_flow        f ON f.id = o.approval_flow_id
LEFT JOIN expense_request er
  ON f.parent_module_name = 'ExpenseRequest'
 AND er.id = CAST(f.parent_id AS INTEGER)
WHERE m.status = 'Waiting' AND o.status = 'Active' AND f.status = 'Pending';
