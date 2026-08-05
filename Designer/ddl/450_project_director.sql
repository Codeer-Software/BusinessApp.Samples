-- 450_project_director.sql — 案件管理の部長開放（ADR-0046・2026-08-06 レビュー第9弾）
-- 「部長である」という組織事実のキャッシュ列 is_director を追加する（is_approver と同格。
-- 「部長→案件編集可」という認可ポリシー自体は Project モジュールの UserWriteCondition に宣言する）。
-- 400 で作成した department_members のトリガーを is_approver + is_director の両方を保守する形に置き換える。
-- 注意: ALTER TABLE ... ADD COLUMN は一度きり（再実行不可）。再計算のみは 385。

ALTER TABLE app_users ADD COLUMN is_director INTEGER NOT NULL DEFAULT 0;

DROP TRIGGER IF EXISTS trg_dept_members_approver_ai;
DROP TRIGGER IF EXISTS trg_dept_members_approver_au;
DROP TRIGGER IF EXISTS trg_dept_members_approver_ad;

CREATE TRIGGER trg_dept_members_approver_ai AFTER INSERT ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL)),
    is_director = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role = 'director')
  WHERE id = NEW.user_id;
END;

CREATE TRIGGER trg_dept_members_approver_au AFTER UPDATE ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL)),
    is_director = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role = 'director')
  WHERE id IN (OLD.user_id, NEW.user_id);
END;

CREATE TRIGGER trg_dept_members_approver_ad AFTER DELETE ON department_members
BEGIN
  UPDATE app_users SET
    is_approver = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
               OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                         WHERE tm.approver_user_id = app_users.id
                           AND (tm.approver_role = 'user' OR tm.approver_role IS NULL)),
    is_director = EXISTS(SELECT 1 FROM department_members m
                         WHERE m.user_id = app_users.id AND m.role = 'director')
  WHERE id = OLD.user_id;
END;

-- 初期一括計算（以後はトリガーが保守）
UPDATE app_users SET
  is_director = EXISTS(SELECT 1 FROM department_members m
                       WHERE m.user_id = app_users.id AND m.role = 'director');
