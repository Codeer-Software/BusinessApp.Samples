-- 440_my_application_view.sql — 申請中一覧のビュー化（2026-08-06 レビュー第9弾）
-- 汎用承認（approval_flow）は件名・金額を持たないため、申請種別ごとの親伝票を JOIN して
-- 「件名・金額」を一覧に出せるようにする（approval_inbox_view と同じビュー方式）。
-- 申請種別が増えたら LEFT JOIN と CASE を追記する（承認エンジン汎用化=docs/10 既知の将来課題）。
-- 再実行可（CREATE VIEW IF NOT EXISTS。作り直すときは DROP してから）。

DROP VIEW IF EXISTS my_application_view;

CREATE VIEW my_application_view AS
SELECT
  f.id                 AS id,
  f.parent_module_name AS parent_module_name,
  f.parent_id          AS parent_id,
  f.status             AS status,
  f.creator            AS creator,
  f.created_at         AS created_at,
  f.current_approver   AS current_approver,
  CASE WHEN f.parent_module_name = 'ExpenseRequest' THEN er.title  END AS title,
  CASE WHEN f.parent_module_name = 'ExpenseRequest' THEN er.amount END AS amount
FROM approval_flow f
LEFT JOIN expense_request er
  ON f.parent_module_name = 'ExpenseRequest'
 AND er.id = CAST(f.parent_id AS INTEGER);
