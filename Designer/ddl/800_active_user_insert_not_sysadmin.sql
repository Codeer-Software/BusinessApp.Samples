-- 800: 認証ビュー経由で作られるユーザーを「システム管理者」にしない（BUG-0397）
--
-- `active_app_users` は認証（ログイン）が読むビューで、INSTEAD OF INSERT のトリガが
-- `app_users` へ実体を作る。そのトリガが **`is_sysadmin` に無条件で 1 を入れていた**。
--
-- いまこのビューへ INSERT するモジュールは無い（`AppUser.DbTable` は `app_users`）ので休眠中だが、
-- 「いつか誰かがビュー経由でユーザーを作る」ときの既定が**システム管理者**というのは危険側の既定である。
-- 職務分掌（ADR-0041: sysadmin は業務に参加しない専任）とも噛み合わない。
--
-- 既定を 0 にする。管理者権限はユーザー管理の画面で明示的に付ける。
DROP TRIGGER IF EXISTS trg_active_app_users_ins;
CREATE TRIGGER trg_active_app_users_ins INSTEAD OF INSERT ON active_app_users
BEGIN
  INSERT INTO app_users (user_name, name, hash, salt, department_id, is_sysadmin, can_use_expense, can_use_timesheet)
  VALUES (new.user_name, new.name, new.hash, new.salt, new.department_id, 0, 0, 0);
END;
