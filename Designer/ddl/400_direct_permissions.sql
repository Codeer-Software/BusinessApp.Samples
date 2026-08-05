-- 400_direct_permissions.sql — 機能権限の直接付与化（ADR-0043、BusinessAppSQLite）
-- 3軸分離: 機能権限 = app_users の直接フラグ／組織所属 = departments + department_members／承認構造 = 課長・部長行。
-- has_sales_access / has_accounting_access は「導出キャッシュ」から「直接付与の真実」へ昇格
-- （列名不変・現在値を初期値として引き継ぐため、移行時点で誰の見え方も変わらない）。
-- is_approver だけは導出キャッシュのまま（決裁権を持つことの帰結。課長/部長行 ∨ 承認テンプレートの個人指名）。
-- 注意: ALTER TABLE ... ADD/DROP COLUMN は一度きり（再実行不可）。is_approver の再計算のみは 385 を使う。

-- ---- 1. 全社員機能のオプトアウトフラグ（既定 ON） ----
ALTER TABLE app_users ADD COLUMN can_use_expense INTEGER NOT NULL DEFAULT 1;
ALTER TABLE app_users ADD COLUMN can_use_timesheet INTEGER NOT NULL DEFAULT 1;

-- 職務分掌: システム管理者アカウントは業務機能を持たない（2アカウント運用推奨・ADR-0041 §3）
UPDATE app_users SET can_use_expense = 0, can_use_timesheet = 0 WHERE is_sysadmin = 1;

-- ---- 2. 旧転記トリガーの撤去（sales/accounting の導出は廃止） ----
DROP TRIGGER IF EXISTS trg_dept_members_perm_ai;
DROP TRIGGER IF EXISTS trg_dept_members_perm_au;
DROP TRIGGER IF EXISTS trg_dept_members_perm_ad;
DROP TRIGGER IF EXISTS trg_departments_perm_au;

-- ---- 3. is_approver 専用の転記トリガー（経路非依存: CLB Submit / sql CLI / 掃除 SQL すべてで発火） ----
CREATE TRIGGER trg_dept_members_approver_ai AFTER INSERT ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL))
  WHERE id = NEW.user_id;
END;

CREATE TRIGGER trg_dept_members_approver_au AFTER UPDATE ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL))
  WHERE id IN (OLD.user_id, NEW.user_id);
END;

CREATE TRIGGER trg_dept_members_approver_ad AFTER DELETE ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL))
  WHERE id = OLD.user_id;
END;

-- テンプレ個人指名の増減にも追随（ADR-0041 既知課題「個人指名承認者に承認者フレームが見えない」の解消）
CREATE TRIGGER trg_tmpl_members_approver_ai AFTER INSERT ON approval_flow_template_member
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL))
  WHERE id = NEW.approver_user_id;
END;

CREATE TRIGGER trg_tmpl_members_approver_au AFTER UPDATE ON approval_flow_template_member
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL))
  WHERE id IN (OLD.approver_user_id, NEW.approver_user_id);
END;

CREATE TRIGGER trg_tmpl_members_approver_ad AFTER DELETE ON approval_flow_template_member
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL))
  WHERE id = OLD.approver_user_id;
END;

-- ---- 4. dept_type 廃止（唯一の消費者＝旧転記トリガーが消えたため。組織マスタから権限概念を除去） ----
ALTER TABLE departments DROP COLUMN dept_type;

-- ---- 5. active_app_users ビューの作り直し（SELECT * の列リストは作成時固定のため）＋初期 admin の業務フラグ OFF ----
DROP TRIGGER IF EXISTS trg_active_app_users_ins;
DROP VIEW IF EXISTS active_app_users;

CREATE VIEW active_app_users AS
SELECT * FROM app_users WHERE is_active = 1;

-- 初期ユーザー seed（CookieAuthentication.CreateInitialUserAsync）はこのビューに INSERT する。
-- 空 DB に最初に作られるのは管理者なので is_sysadmin=1・業務フラグ OFF を付与する。
CREATE TRIGGER trg_active_app_users_ins INSTEAD OF INSERT ON active_app_users
BEGIN
  INSERT INTO app_users (user_name, name, hash, salt, department_id, is_sysadmin, can_use_expense, can_use_timesheet)
  VALUES (new.user_name, new.name, new.hash, new.salt, new.department_id, 1, 0, 0);
END;

-- ---- 6. is_approver の初期一括再計算（テンプレ個人指名を新たに算入。以後はトリガーが保守） ----
UPDATE app_users SET
  is_approver = EXISTS(SELECT 1 FROM department_members m
                       WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
             OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                       WHERE tm.approver_user_id = app_users.id
                         AND (tm.approver_role = 'user' OR tm.approver_role IS NULL));
