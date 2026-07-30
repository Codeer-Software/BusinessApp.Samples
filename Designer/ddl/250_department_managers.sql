-- 250_department_managers.sql — 1部門に複数の課長・部長（ADR-0016、BusinessAppSQLite）
-- 設計: docs/decisions/0016-1部門複数課長部長の承認ルート.md
-- 旧 departments.manager_user / director_user は使用停止（物理削除しない。参照はモジュール・スクリプトから全除去）

-- 部門役職者（1部門に課長・部長を複数登録できる）
CREATE TABLE IF NOT EXISTS department_managers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    department_id INTEGER REFERENCES departments(id),
    user_id INTEGER REFERENCES app_users(id),
    role TEXT NOT NULL                -- 'manager'(課長) / 'director'(部長)
);
CREATE INDEX IF NOT EXISTS idx_department_managers_dept ON department_managers(department_id);

-- 承認段階の完了判定区分: NULL/'all'=必須全員(従来) / 'any'=1人承認で完了(役職展開の並列承認)
ALTER TABLE approval_flow_order ADD COLUMN approval_type TEXT;

-- 承認待ち受信箱（メンバーベース）: 「Active な Order で自分が Waiting のもの」だけが出る。
-- 列名は旧 ApprovalInbox(DbTable=approval_flow) と互換に揃え、モジュール側は DbTable 差し替えのみで移行する。
CREATE VIEW IF NOT EXISTS approval_inbox_view AS
SELECT m.id                 AS id,
       f.parent_module_name AS parent_module_name,
       f.parent_id          AS parent_id,
       f.status             AS status,
       m.approver_user_id   AS current_approver,
       f.creator            AS creator,
       f.created_at         AS created_at
FROM approval_flow_member m
JOIN approval_flow_order  o ON o.id = m.approval_flow_order_id
JOIN approval_flow        f ON f.id = o.approval_flow_id
WHERE m.status = 'Waiting' AND o.status = 'Active' AND f.status = 'Pending';

-- ---- 移行: 既存の単一列 → department_managers（冪等） ----
INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, d.manager_user, 'manager'
FROM departments d
WHERE d.manager_user IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM department_managers x
                  WHERE x.department_id = d.id AND x.user_id = d.manager_user AND x.role = 'manager');

INSERT INTO department_managers (department_id, user_id, role)
SELECT d.id, d.director_user, 'director'
FROM departments d
WHERE d.director_user IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM department_managers x
                  WHERE x.department_id = d.id AND x.user_id = d.director_user AND x.role = 'director');
