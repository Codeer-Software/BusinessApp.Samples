-- 570_password_change_view_update.sql — 自己パスワード変更のためのビュー更新トリガー（ADR-0059）
--
-- 認証のユーザーテーブルは appsettings.json の PasswordCheckUserTableInfo で
-- active_app_users（ビュー）を指している（ddl/290・390）。SQLite のビューは
-- INSTEAD OF トリガーが無いと更新できないため、seed の INSERT にはトリガーがあるが
-- UPDATE には無く、パスワード変更 API がビュー越しに hash/salt を書けなかった。
--
-- app_users を直接名指しすれば動くが、それだと「認証テーブルは設定で差し替えられる」
-- という契約を壊す（appsettings を変えても API 側が追随しない）。INSERT と同じく
-- ビュー側にトリガーを置き、サーバは常に設定のテーブル名だけを見る形にする。
CREATE TRIGGER IF NOT EXISTS trg_active_app_users_upd_password
INSTEAD OF UPDATE OF hash, salt ON active_app_users
BEGIN
  UPDATE app_users SET hash = new.hash, salt = new.salt WHERE id = old.id;
END;
