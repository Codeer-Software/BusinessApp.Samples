-- 390_drop_role.sql — 旧ロール列の廃止（部品アーキテクチャ再編 P7・並走期間の終了、BusinessAppSQLite）
-- 権限の真実は department_members（ユーザー×部門×member/manager/director）と app_users.is_sysadmin に
-- 一本化された（380 参照）。全フレーム・全モジュール条件・全スクリプトの Role 参照は除去済み。
-- active_app_users ビューは SELECT * の列リストが作成時に固定されるため、DROP COLUMN の前後で作り直す。
-- 注意: 一度きり（再実行不可）。

DROP TRIGGER IF EXISTS trg_active_app_users_ins;
DROP VIEW IF EXISTS active_app_users;

ALTER TABLE app_users DROP COLUMN role;

CREATE VIEW active_app_users AS
SELECT * FROM app_users WHERE is_active = 1;

-- 初期ユーザー seed（CookieAuthentication.CreateInitialUserAsync）はこのビューに
-- (user_name, hash, salt) だけを INSERT する。空 DB に最初に作られるのは管理者なので is_sysadmin=1 を付与する。
CREATE TRIGGER trg_active_app_users_ins INSTEAD OF INSERT ON active_app_users
BEGIN
  INSERT INTO app_users (user_name, name, hash, salt, department_id, is_sysadmin)
  VALUES (new.user_name, new.name, new.hash, new.salt, new.department_id, 1);
END;
