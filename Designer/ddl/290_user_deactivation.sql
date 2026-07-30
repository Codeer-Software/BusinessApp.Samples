-- 290_user_deactivation.sql — 退職者アカウントの無効化（Q4 / レビュー第4弾、BusinessAppSQLite）
-- 退職者は削除せず is_active=0 にする（過去の仕訳・申請・承認履歴の参照を守るため物理削除しない）。
--
-- ログイン拒否の仕組み:
--   appsettings.json の PasswordCheckUserTableInfo.TableName を active_app_users（ビュー）に変更。
--   認証時の SELECT がビューを通るため、is_active=0 のユーザーはパスワードが合っても弾かれる。
--   Server の初期ユーザー seed（admin 自動作成）は同じ TableName に INSERT するため、
--   INSTEAD OF INSERT トリガーでビューへの INSERT を app_users に転送する（新規DB構築でも壊れない）。

ALTER TABLE app_users ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1;

CREATE VIEW IF NOT EXISTS active_app_users AS
SELECT * FROM app_users WHERE is_active = 1;

CREATE TRIGGER IF NOT EXISTS trg_active_app_users_ins INSTEAD OF INSERT ON active_app_users
BEGIN
  INSERT INTO app_users (user_name, name, hash, salt, department_id, role)
  VALUES (new.user_name, new.name, new.hash, new.salt, new.department_id, new.role);
END;
