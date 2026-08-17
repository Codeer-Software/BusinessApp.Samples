-- 620_team_time_entry_view.sql — 工数の閲覧範囲を「本人／課長／部長」に開くファンアウト・ビュー（BUG-0217）
--
-- 背景: time_entries を直接見るモジュール（TimeEntry）は DataReadCondition を
--   UserRef.Value == CurrentUser.Id.Value に閉じてあり、本人の工数しか読めない。
--   CLB の DataReadCondition は自モジュールの列しか見られないため
--   「自分の行 ∨ 部下の行」という OR を 1 モジュールでは書けない。
--   そこで「明細 × 閲覧可能者」のファンアウト・ビューを作り、viewer_user_id 列に対して
--   ViewerUser.Value == CurrentUser.Id.Value という単一条件を書く（approval_inbox_view と同じ形）。
--
-- 閲覧可能者の定義（上司の対応付けの正は department_members.role。
--   departments.manager_user / director_user は全 16 行 NULL の死列で、実データ上の根拠にならない）:
--   1) 本人                — time_entries.user_id 当人
--   2) 課長（manager）      — 対象者が所属するノードと**同一ノード**に role='manager' 行を持つ人
--   3) 部長（director）     — 対象者が所属するノードの**自ノードを含む祖先**に role='director' 行を持つ人
--                             （課 → 親の部 と辿るので、部長は配下全課のメンバー＝課長本人ぶんも見える）
--   判定は常に「現在の所属」（department_members の今の姿）で、異動前の当時の上司は見えない。
--   兼務（同一人物が複数ノードに member 行を持つ）は、そのすべての上司から見える。
--
-- 読み取り専用（INSTEAD OF トリガは作らない）。書き込みは従来どおり
--   本人 = TimeEntry（DataWriteCondition で本人に限定）／経理 = TimeEntryAdmin。
-- id は time_entries.id をそのまま採る。同一 viewer_user_id の中では id は一意
--   （1 明細 × 1 閲覧者 = 1 行）なので、DataReadCondition で viewer を必ず 1 人に絞る限り
--   モジュールの主キーとして安全に使える。
-- 再実行可（DROP → CREATE）。

DROP VIEW IF EXISTS team_time_entry_view;

CREATE VIEW team_time_entry_view AS
WITH RECURSIVE dept_self_ancestor(node_id, ancestor_id) AS (
    SELECT id, id FROM departments
  UNION ALL
    SELECT a.node_id, d.parent_id
    FROM dept_self_ancestor a
    JOIN departments d ON d.id = a.ancestor_id
    WHERE d.parent_id IS NOT NULL
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
