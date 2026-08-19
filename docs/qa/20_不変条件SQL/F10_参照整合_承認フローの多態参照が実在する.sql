-- 何を保証するか: 承認フローの多態参照（`approval_flow.parent_module_name` / `parent_id` は TEXT で FK ではない）が
--                 実在する申請を指し、段・メンバー・履歴が親を失っていないこと。申請側からの逆向きも見る。
-- 違反時の意味: BUG-0413 の再発。`Designer/ddl/810` のコメント自身が
--               「**`parent_id` は TEXT で FK ではないため、参照整合の検査（F01）にも引っかからない**」と
--               書いているとおり、**F01 が構造的に見られない領域**。
--               削除した申請の承認履歴（個人名つき）が残り続け、承認まわりを調べるたびにノイズになる。
--               トリガ 810 / 813 が消し漏らしたときの唯一の検出手段。
-- 出典: docs/qa/02_バグ台帳.md BUG-0413 ／ Designer/ddl/810・813

SELECT '承認フローの親が実在しない' AS 違反, f.id AS フローid, f.parent_module_name AS 親モジュール,
       f.parent_id AS 親id, f.status AS 状態, f.created_at AS 作成日時
FROM approval_flow f
WHERE f.parent_module_name = 'ExpenseRequest'
  AND NOT EXISTS (SELECT 1 FROM expense_request er WHERE er.id = CAST(f.parent_id AS INTEGER))

UNION ALL
SELECT '承認の段が親フローを失っている', o.id, 'approval_flow', CAST(o.approval_flow_id AS TEXT), o.status, NULL
FROM approval_flow_order o
WHERE o.approval_flow_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM approval_flow f WHERE f.id = o.approval_flow_id)

UNION ALL
SELECT '承認メンバーが段を失っている', m.id, 'approval_flow_order', CAST(m.approval_flow_order_id AS TEXT), m.status, NULL
FROM approval_flow_member m
WHERE m.approval_flow_order_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM approval_flow_order o WHERE o.id = m.approval_flow_order_id)

UNION ALL
SELECT '承認メンバーがフローを失っている', m.id, 'approval_flow', CAST(m.approval_flow_id AS TEXT), m.status, NULL
FROM approval_flow_member m
WHERE m.approval_flow_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM approval_flow f WHERE f.id = m.approval_flow_id)

UNION ALL
SELECT '承認履歴がフローを失っている', h.id, 'approval_flow', CAST(h.approval_flow_id AS TEXT), NULL, NULL
FROM approval_history h
WHERE h.approval_flow_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM approval_flow f WHERE f.id = h.approval_flow_id)

UNION ALL
SELECT '申請の承認フローが実在しない', er.id, 'approval_flow', CAST(er.approval_flow_id AS TEXT), er.settlement_status, NULL
FROM expense_request er
WHERE er.approval_flow_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM approval_flow f WHERE f.id = er.approval_flow_id)
