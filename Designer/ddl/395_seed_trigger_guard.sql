-- 395_seed_trigger_guard.sql — 初期ユーザー seed トリガーの安全化（レビュー指摘 C-2、BusinessAppSQLite）
-- 390 で作成した active_app_users の INSTEAD OF INSERT トリガーは、ビュー経由の全 INSERT に
-- is_sysadmin=1 を付与していた。現状の経路は「空 DB の初期 admin 作成」のみだが、将来ビュー経由の
-- 登録経路が増えた場合に全員が管理者になる罠を塞ぐ: 管理者フラグは「最初の1人（=空テーブルへの INSERT）」のみ。

DROP TRIGGER IF EXISTS trg_active_app_users_ins;

CREATE TRIGGER trg_active_app_users_ins INSTEAD OF INSERT ON active_app_users
BEGIN
  INSERT INTO app_users (user_name, name, hash, salt, department_id, is_sysadmin)
  VALUES (new.user_name, new.name, new.hash, new.salt, new.department_id,
          CASE WHEN (SELECT COUNT(*) FROM app_users) = 0 THEN 1 ELSE 0 END);
END;
