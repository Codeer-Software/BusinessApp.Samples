-- 825_dept_recursion_depth_guard.sql — 部門階層の再帰に深さ上限を付ける（BUG-0467）
--
-- `team_time_entry_view` の `dept_self_ancestor` は `UNION ALL` でサイクル検出も深さ上限も無い。
-- `departments.parent_id` に自己参照（`parent_id = id`）や循環（A→B→A）が 1 つでも入ると、
-- **チーム工数一覧を開いた瞬間に無限ループしてメモリを食い潰す**——
-- 静かな誤りではなく、画面のハングという形で出る。
--
-- 循環を防ぐガードは**画面（`Department.mod.cs` の `ParentRef_OnDataChanged`）にしか無い**ので、
-- SQL 直投入・移行・部門マスタの一括更新は素通りする。
-- 制約では表現できない（CHECK は自己参照までしか書けず、循環は書けない）ので、
-- **ビュー側で確実に止める**。実運用の階層は 2 段（部→課）なので、上限 10 は十分に余裕がある。
-- 循環そのものの検出は不変条件 `F12` が受け持つ。

DROP VIEW IF EXISTS team_time_entry_view;

CREATE VIEW team_time_entry_view AS
WITH RECURSIVE dept_self_ancestor(node_id, ancestor_id, depth) AS (
    SELECT id, id, 0 FROM departments
  UNION ALL
    SELECT a.node_id, d.parent_id, a.depth + 1
    FROM dept_self_ancestor a
    JOIN departments d ON d.id = a.ancestor_id
    WHERE d.parent_id IS NOT NULL
      -- **深さ上限**（BUG-0467）。循環があってもここで必ず止まる
      AND a.depth < 10
),
time_entry_viewer(user_id, viewer_user_id) AS (
    -- 本人
    SELECT u.id, u.id FROM app_users u
  UNION
    -- 課長: 同一ノードの manager
    SELECT m.user_id, mg.user_id
    FROM department_members m
    JOIN department_members mg
      ON mg.department_id = m.department_id AND mg.role = 'manager'
  UNION
    -- 部長: 所属ノードの祖先（自ノードを含む）の director
    SELECT m.user_id, dr.user_id
    FROM department_members m
    JOIN dept_self_ancestor a ON a.node_id = m.department_id
    JOIN department_members dr
      ON dr.department_id = a.ancestor_id AND dr.role = 'director'
)
SELECT
  t.id         AS id,
  t.user_id    AS user_id,
  t.project_id AS project_id,
  t.work_date  AS work_date,
  t.minutes    AS minutes,
  t.note       AS note,
  t.creator    AS creator,
  t.updater    AS updater,
  t.created_at AS created_at,
  t.updated_at AS updated_at,
  v.viewer_user_id AS viewer_user_id
FROM time_entries t
JOIN time_entry_viewer v ON v.user_id = t.user_id;

SELECT COUNT(*) AS rows_in_view FROM team_time_entry_view;
