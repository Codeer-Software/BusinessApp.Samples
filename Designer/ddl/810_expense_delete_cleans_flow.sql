-- 810: 経費申請（下書き）を消したら承認フローも一緒に消す（BUG-0413）
--
-- `ExpenseRequest.DeleteDraft_OnClick` のコメントが「**既知の限界**: 承認フロー（子）の行は
-- スクリプトから物理削除できず孤児として残る（実測 2026-07-16）」と宣言しているとおり、
-- 下書きを削除するたびに `approval_flow`（と配下の段・メンバー・履歴）が置き去りになる。
--
-- 画面には出ないので害が見えにくいが、
--   ・削除した申請の承認履歴が DB に残り続ける（個人名つき）
--   ・行数が増え続け、承認まわりの調査のたびにノイズになる
--   ・`parent_id` は TEXT で FK ではないため、参照整合の検査（F01）にも引っかからない
-- ——という「静かに溜まる」型の汚れである。**スクリプトで消せないなら DB 側で消す。**
--
-- `expense_request` の DELETE は下書きに限られている（`DeleteDraft_OnClick` のガード）ので、
-- このトリガが確定済みの申請の履歴を消すことはない。
DROP TRIGGER IF EXISTS trg_expense_request_delete_flow;
CREATE TRIGGER trg_expense_request_delete_flow AFTER DELETE ON expense_request
BEGIN
  DELETE FROM approval_history
   WHERE approval_flow_id IN (
     SELECT id FROM approval_flow
      WHERE parent_module_name = 'ExpenseRequest' AND CAST(parent_id AS INTEGER) = old.id);

  -- メンバー行は `approval_flow_id` が空のことがあり（段だけを親に持つ行）、
  -- 段（`approval_flow_order_id`）からも辿らないと消し残して FK に引っかかる（実測 2026-08-19）
  DELETE FROM approval_flow_member
   WHERE approval_flow_id IN (
     SELECT id FROM approval_flow
      WHERE parent_module_name = 'ExpenseRequest' AND CAST(parent_id AS INTEGER) = old.id)
      OR approval_flow_order_id IN (
     SELECT o.id FROM approval_flow_order o JOIN approval_flow f ON f.id = o.approval_flow_id
      WHERE f.parent_module_name = 'ExpenseRequest' AND CAST(f.parent_id AS INTEGER) = old.id);

  DELETE FROM approval_flow_order
   WHERE approval_flow_id IN (
     SELECT id FROM approval_flow
      WHERE parent_module_name = 'ExpenseRequest' AND CAST(parent_id AS INTEGER) = old.id);

  DELETE FROM approval_flow
   WHERE parent_module_name = 'ExpenseRequest' AND CAST(parent_id AS INTEGER) = old.id;
END;

-- 既に残っている孤児を掃除する（2026-08-19 時点で 1 件）
DELETE FROM approval_history
 WHERE approval_flow_id IN (
   SELECT f.id FROM approval_flow f
    WHERE f.parent_module_name = 'ExpenseRequest'
      AND NOT EXISTS (SELECT 1 FROM expense_request e WHERE e.id = CAST(f.parent_id AS INTEGER)));

DELETE FROM approval_flow_member
 WHERE approval_flow_id IN (
   SELECT f.id FROM approval_flow f
    WHERE f.parent_module_name = 'ExpenseRequest'
      AND NOT EXISTS (SELECT 1 FROM expense_request e WHERE e.id = CAST(f.parent_id AS INTEGER)))
    OR approval_flow_order_id IN (
   SELECT o.id FROM approval_flow_order o JOIN approval_flow f ON f.id = o.approval_flow_id
    WHERE f.parent_module_name = 'ExpenseRequest'
      AND NOT EXISTS (SELECT 1 FROM expense_request e WHERE e.id = CAST(f.parent_id AS INTEGER)));

DELETE FROM approval_flow_order
 WHERE approval_flow_id IN (
   SELECT f.id FROM approval_flow f
    WHERE f.parent_module_name = 'ExpenseRequest'
      AND NOT EXISTS (SELECT 1 FROM expense_request e WHERE e.id = CAST(f.parent_id AS INTEGER)));

DELETE FROM approval_flow
 WHERE parent_module_name = 'ExpenseRequest'
   AND NOT EXISTS (SELECT 1 FROM expense_request e WHERE e.id = CAST(parent_id AS INTEGER));
