-- 380_membership_permissions.sql — 権限モデル刷新: 部門メンバーシップ×システム管理者フラグ（部品アーキテクチャ再編、BusinessAppSQLite）
-- 設計（プラン: pageframe-adaptive-eagle / ADR は P8 で起票）:
--   真実は「department_members（ユーザー×部門×権限 member/manager/director）」と「app_users.is_sysadmin」の2つだけ。
--   フレームゲート用の app_users.has_sales_access / has_accounting_access / is_approver は
--   トリガーが保守する導出キャッシュ（select_label と同じ経路非依存パターン）。手で更新しない。
--   旧 role 列は並走（条件スイープ完了・全検証通過後に廃止）。
-- 注意: ALTER TABLE ... RENAME / ADD COLUMN は一度きり（再実行不可）。キャッシュ再計算だけの再実行は 385 を使う。

-- ---- 1. 部門役職者 → 部門メンバー（一般化） ----
ALTER TABLE department_managers RENAME TO department_members;

DROP INDEX IF EXISTS idx_department_managers_dept;
DROP INDEX IF EXISTS ux_department_managers_dept_user_role;
CREATE INDEX IF NOT EXISTS idx_department_members_dept ON department_members(department_id);
CREATE INDEX IF NOT EXISTS idx_department_members_user ON department_members(user_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_department_members_dept_user_role
    ON department_members(department_id, user_id, role);

-- ---- 2. 部門種別（転記トリガーだけが参照。フレーム定義は部門名を知らない） ----
ALTER TABLE departments ADD COLUMN dept_type TEXT NOT NULL DEFAULT 'other';

UPDATE departments SET dept_type = CASE
  WHEN name IN ('総務部') THEN 'accounting'
  WHEN name IN ('営業部', 'SaaS事業部') THEN 'sales'
  WHEN name LIKE '開発%' THEN 'dev'
  ELSE 'other'
END;

-- ---- 3. app_users: 管理者フラグ（真実）＋ゲート用キャッシュ列（導出） ----
ALTER TABLE app_users ADD COLUMN is_sysadmin INTEGER NOT NULL DEFAULT 0;
ALTER TABLE app_users ADD COLUMN has_sales_access INTEGER NOT NULL DEFAULT 0;
ALTER TABLE app_users ADD COLUMN has_accounting_access INTEGER NOT NULL DEFAULT 0;
ALTER TABLE app_users ADD COLUMN is_approver INTEGER NOT NULL DEFAULT 0;

-- 旧 role からの一度きりの移行（is_sysadmin のみ。他は role に依存せずメンバー行から導出する）
UPDATE app_users SET is_sysadmin = 1 WHERE role = 'sysadmin';

-- ---- 4. メンバー行の初期移行 ----
-- 主所属部門があり、その部門にまだ行が無いユーザーへ「一般(member)」行を付与。
-- 既存の manager/director 行はそれ自体がメンバーシップとして数えられるため重複追加しない（冪等）。
INSERT INTO department_members (department_id, user_id, role)
SELECT u.department_id, u.id, 'member'
FROM app_users u
WHERE u.department_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM department_members m
                  WHERE m.department_id = u.department_id AND m.user_id = u.id);

-- ---- 5. キャッシュ保守トリガー（経路非依存: CLB Submit / sql CLI / デモ掃除 SQL すべてで発火） ----
DROP TRIGGER IF EXISTS trg_dept_members_perm_ai;
DROP TRIGGER IF EXISTS trg_dept_members_perm_au;
DROP TRIGGER IF EXISTS trg_dept_members_perm_ad;
DROP TRIGGER IF EXISTS trg_departments_perm_au;

CREATE TRIGGER trg_dept_members_perm_ai AFTER INSERT ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director')),
    has_sales_access = EXISTS(SELECT 1 FROM department_members m
                              JOIN departments d ON d.id = m.department_id
                              WHERE m.user_id = app_users.id AND d.dept_type = 'sales'),
    has_accounting_access = EXISTS(SELECT 1 FROM department_members m
                                   JOIN departments d ON d.id = m.department_id
                                   WHERE m.user_id = app_users.id AND d.dept_type = 'accounting')
  WHERE id = NEW.user_id;
END;

CREATE TRIGGER trg_dept_members_perm_au AFTER UPDATE ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director')),
    has_sales_access = EXISTS(SELECT 1 FROM department_members m
                              JOIN departments d ON d.id = m.department_id
                              WHERE m.user_id = app_users.id AND d.dept_type = 'sales'),
    has_accounting_access = EXISTS(SELECT 1 FROM department_members m
                                   JOIN departments d ON d.id = m.department_id
                                   WHERE m.user_id = app_users.id AND d.dept_type = 'accounting')
  WHERE id IN (OLD.user_id, NEW.user_id);
END;

CREATE TRIGGER trg_dept_members_perm_ad AFTER DELETE ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director')),
    has_sales_access = EXISTS(SELECT 1 FROM department_members m
                              JOIN departments d ON d.id = m.department_id
                              WHERE m.user_id = app_users.id AND d.dept_type = 'sales'),
    has_accounting_access = EXISTS(SELECT 1 FROM department_members m
                                   JOIN departments d ON d.id = m.department_id
                                   WHERE m.user_id = app_users.id AND d.dept_type = 'accounting')
  WHERE id = OLD.user_id;
END;

-- 部門種別の変更は、その部門にメンバー行を持つ全ユーザーへ波及
CREATE TRIGGER trg_departments_perm_au AFTER UPDATE ON departments
WHEN OLD.dept_type IS NOT NEW.dept_type
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director')),
    has_sales_access = EXISTS(SELECT 1 FROM department_members m
                              JOIN departments d ON d.id = m.department_id
                              WHERE m.user_id = app_users.id AND d.dept_type = 'sales'),
    has_accounting_access = EXISTS(SELECT 1 FROM department_members m
                                   JOIN departments d ON d.id = m.department_id
                                   WHERE m.user_id = app_users.id AND d.dept_type = 'accounting')
  WHERE id IN (SELECT user_id FROM department_members WHERE department_id = NEW.id);
END;

-- ---- 6. 初期一括計算（385 と同内容。以後はトリガーが保守） ----
UPDATE app_users SET
  is_approver = EXISTS(SELECT 1 FROM department_members m
                       WHERE m.user_id = app_users.id AND m.role IN ('manager','director')),
  has_sales_access = EXISTS(SELECT 1 FROM department_members m
                            JOIN departments d ON d.id = m.department_id
                            WHERE m.user_id = app_users.id AND d.dept_type = 'sales'),
  has_accounting_access = EXISTS(SELECT 1 FROM department_members m
                                 JOIN departments d ON d.id = m.department_id
                                 WHERE m.user_id = app_users.id AND d.dept_type = 'accounting');
