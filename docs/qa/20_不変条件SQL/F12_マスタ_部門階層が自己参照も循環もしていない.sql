-- 何を保証するか: 部門の親子が (a) 自己参照していない (b) 3 階層以上になっていない (c) 循環していないこと。
-- 違反時の意味:
--   (a)(c) `team_time_entry_view` の再帰 CTE が**無限ループする**。深さ上限は `ddl/825` で入れたので
--          ハングはしなくなったが、**祖先の解決が途中で切れる**ので部長にチーム工数が見えなくなる。
--          承認の walk-up（部長を祖先方向に探す処理）も同じ形で壊れる。
--   (b)   `trg_app_users_bizdept_ai/au` は `CASE WHEN d.parent_id IS NULL THEN d.id ELSE d.parent_id END` と
--          **1 段しか遡らない**ので、課の下の課に所属するユーザーの `business_department_id` が
--          **部ではなく中間の課**を指し、伝票部門が壊れる（結果は A10 が拾うが、原因はここでしか分からない）。
-- なぜ制約でやらないか: `CHECK (parent_id <> id)` は書けるがテーブル再構築が要り、
--                       **循環は CHECK では表現できない**。ガードは画面（`Department.mod.cs`）にしか無く、
--                       SQL 直投入・移行・一括更新は素通りする。
-- 出典: docs/qa/02_バグ台帳.md BUG-0467 ／ Designer/ddl/410・620・825

SELECT '自分自身を親にしている' AS 違反, d.id AS 部門id, d.name AS 部門名,
       d.parent_id AS 親id, NULL AS 深さ
FROM departments d
WHERE d.parent_id = d.id

UNION ALL

-- 3 階層以上（部→課→課）。運用は 2 階層まで
SELECT '3 階層以上になっている', d.id, d.name, d.parent_id, 3
FROM departments d
JOIN departments p  ON p.id = d.parent_id
JOIN departments gp ON gp.id = p.parent_id

UNION ALL

-- 循環。深さ上限を付けた再帰で「上限まで辿っても根に着かない」ノードを拾う
SELECT '親をたどると循環している', w.start_id, d.name, d.parent_id, w.depth
FROM (
  WITH RECURSIVE walk(start_id, cur_id, depth) AS (
    SELECT id, parent_id, 1 FROM departments WHERE parent_id IS NOT NULL
    UNION ALL
    SELECT w.start_id, d.parent_id, w.depth + 1
    FROM walk w JOIN departments d ON d.id = w.cur_id
    WHERE d.parent_id IS NOT NULL AND w.depth < 20
  )
  SELECT start_id, MAX(depth) AS depth FROM walk GROUP BY start_id HAVING MAX(depth) >= 20
) w
JOIN departments d ON d.id = w.start_id
