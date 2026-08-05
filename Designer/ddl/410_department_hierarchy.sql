-- 410_department_hierarchy.sql — 部門の部課階層化（ADR-0044、BusinessAppSQLite）
-- departments を自己参照 2 階層にする（parent_id NULL=部 / 非NULL=課。課はオプション）。
-- node_type ('dept'/'section') は parent_id から導出されるキャッシュ（トリガー保守）。
-- app_users.business_department_id は「伝票部門用の祖先の部」キャッシュ（主所属が課なら親の部・部ならそのまま）。
-- 注意: ALTER TABLE ... ADD COLUMN は一度きり（再実行不可）。

-- ---- 1. 階層列 ----
ALTER TABLE departments ADD COLUMN parent_id INTEGER NULL REFERENCES departments(id);
ALTER TABLE departments ADD COLUMN node_type TEXT NOT NULL DEFAULT 'dept';

CREATE TRIGGER trg_departments_nodetype_ai AFTER INSERT ON departments
BEGIN
  UPDATE departments SET node_type = CASE WHEN NEW.parent_id IS NULL THEN 'dept' ELSE 'section' END
  WHERE id = NEW.id;
END;

CREATE TRIGGER trg_departments_nodetype_au AFTER UPDATE OF parent_id ON departments
BEGIN
  UPDATE departments SET node_type = CASE WHEN NEW.parent_id IS NULL THEN 'dept' ELSE 'section' END
  WHERE id = NEW.id;
END;

-- ---- 2. 伝票部門用の祖先「部」キャッシュ ----
ALTER TABLE app_users ADD COLUMN business_department_id INTEGER NULL REFERENCES departments(id);

CREATE TRIGGER trg_app_users_bizdept_ai AFTER INSERT ON app_users
BEGIN
  UPDATE app_users SET business_department_id =
    (SELECT CASE WHEN d.parent_id IS NULL THEN d.id ELSE d.parent_id END
     FROM departments d WHERE d.id = NEW.department_id)
  WHERE id = NEW.id;
END;

CREATE TRIGGER trg_app_users_bizdept_au AFTER UPDATE OF department_id ON app_users
BEGIN
  UPDATE app_users SET business_department_id =
    (SELECT CASE WHEN d.parent_id IS NULL THEN d.id ELSE d.parent_id END
     FROM departments d WHERE d.id = NEW.department_id)
  WHERE id = NEW.id;
END;

-- 部⇔課の付け替え（parent_id 変更）は、そのノードを主所属とする全ユーザーへ波及
CREATE TRIGGER trg_departments_bizdept_au AFTER UPDATE OF parent_id ON departments
BEGIN
  UPDATE app_users SET business_department_id =
    (SELECT CASE WHEN d.parent_id IS NULL THEN d.id ELSE d.parent_id END
     FROM departments d WHERE d.id = app_users.department_id)
  WHERE department_id = NEW.id;
END;

-- ---- 3. シード組織の再編（ユーザー名の「第一課長/第二課長」と構造を一致させる） ----
INSERT INTO departments (code, name, display_order, is_active, parent_id) VALUES
  ('10-1', '総務部 第一課',    101, 1, 2),
  ('10-2', '総務部 第二課',    102, 1, 2),
  ('20-1', '営業部 第一課',    201, 1, 3),
  ('20-2', '営業部 第二課',    202, 1, 3),
  ('31-1', '開発1部 第一課',   311, 1, 4),
  ('31-2', '開発1部 第二課',   312, 1, 4),
  ('32-1', '開発2部 第一課',   321, 1, 5),
  ('32-2', '開発2部 第二課',   322, 1, 5),
  ('40-1', 'SaaS事業部 第一課', 401, 1, 6),
  ('40-2', 'SaaS事業部 第二課', 402, 1, 6);

-- 課長行を各課へ移動（部長・部長代理の director 行は部のまま。
-- kaihatsu1_kacho1 の営業部 member 行=兼務は部直属のまま温存）
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='総務部 第一課')
 WHERE role='manager' AND department_id=2 AND user_id=(SELECT id FROM app_users WHERE user_name='soumu_kacho1');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='総務部 第二課')
 WHERE role='manager' AND department_id=2 AND user_id=(SELECT id FROM app_users WHERE user_name='soumu_kacho2');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='営業部 第一課')
 WHERE role='manager' AND department_id=3 AND user_id=(SELECT id FROM app_users WHERE user_name='eigyo_kacho1');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='営業部 第二課')
 WHERE role='manager' AND department_id=3 AND user_id=(SELECT id FROM app_users WHERE user_name='eigyo_kacho2');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='開発1部 第一課')
 WHERE role='manager' AND department_id=4 AND user_id=(SELECT id FROM app_users WHERE user_name='kaihatsu1_kacho1');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='開発1部 第二課')
 WHERE role='manager' AND department_id=4 AND user_id=(SELECT id FROM app_users WHERE user_name='kaihatsu1_kacho2');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='開発2部 第一課')
 WHERE role='manager' AND department_id=5 AND user_id=(SELECT id FROM app_users WHERE user_name='kaihatsu2_kacho1');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='開発2部 第二課')
 WHERE role='manager' AND department_id=5 AND user_id=(SELECT id FROM app_users WHERE user_name='kaihatsu2_kacho2');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='SaaS事業部 第一課')
 WHERE role='manager' AND department_id=6 AND user_id=(SELECT id FROM app_users WHERE user_name='saas_kacho1');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='SaaS事業部 第二課')
 WHERE role='manager' AND department_id=6 AND user_id=(SELECT id FROM app_users WHERE user_name='saas_kacho2');

-- 一般社員の member 行を所属課へ移動（一般は第一課へ・kaihatsu2_shinjin のみ第二課=課をまたぐ検証用）
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='総務部 第一課')
 WHERE role='member' AND department_id=2 AND user_id=(SELECT id FROM app_users WHERE user_name='soumu_ippan');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='営業部 第一課')
 WHERE role='member' AND department_id=3 AND user_id=(SELECT id FROM app_users WHERE user_name='eigyo_ippan');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='開発1部 第一課')
 WHERE role='member' AND department_id=4 AND user_id=(SELECT id FROM app_users WHERE user_name='kaihatsu1_ippan');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='開発2部 第一課')
 WHERE role='member' AND department_id=5 AND user_id=(SELECT id FROM app_users WHERE user_name='kaihatsu2_ippan');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='開発2部 第二課')
 WHERE role='member' AND department_id=5 AND user_id=(SELECT id FROM app_users WHERE user_name='kaihatsu2_shinjin');
UPDATE department_members SET department_id = (SELECT id FROM departments WHERE name='SaaS事業部 第一課')
 WHERE role='member' AND department_id=6 AND user_id=(SELECT id FROM app_users WHERE user_name='saas_ippan');

-- 主所属（app_users.department_id）を課へ（課長・一般。部長・部長代理・admin・test-test は部/未設定のまま）
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='総務部 第一課')   WHERE user_name IN ('soumu_kacho1','soumu_ippan');
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='総務部 第二課')   WHERE user_name='soumu_kacho2';
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='営業部 第一課')   WHERE user_name IN ('eigyo_kacho1','eigyo_ippan');
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='営業部 第二課')   WHERE user_name='eigyo_kacho2';
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='開発1部 第一課')  WHERE user_name IN ('kaihatsu1_kacho1','kaihatsu1_ippan');
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='開発1部 第二課')  WHERE user_name='kaihatsu1_kacho2';
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='開発2部 第一課')  WHERE user_name IN ('kaihatsu2_kacho1','kaihatsu2_ippan');
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='開発2部 第二課')  WHERE user_name IN ('kaihatsu2_kacho2','kaihatsu2_shinjin');
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='SaaS事業部 第一課') WHERE user_name IN ('saas_kacho1','saas_ippan');
UPDATE app_users SET department_id = (SELECT id FROM departments WHERE name='SaaS事業部 第二課') WHERE user_name='saas_kacho2';

-- ---- 4. 初期一括計算（以後はトリガーが保守） ----
UPDATE departments SET node_type = CASE WHEN parent_id IS NULL THEN 'dept' ELSE 'section' END;
UPDATE app_users SET business_department_id =
  (SELECT CASE WHEN d.parent_id IS NULL THEN d.id ELSE d.parent_id END
   FROM departments d WHERE d.id = app_users.department_id);
