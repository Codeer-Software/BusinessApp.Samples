-- 000_auth.sql — 認証・基盤テーブル（BusinessAppSQLite / business-app_v1.db）
-- app_users: Cookie 認証のユーザーテーブル（PasswordCheckUserTableInfo の契約列 + name）
--   初期ユーザー admin/admin はサーバ起動時に CreateInitialUserAsync が自動投入する
-- temporary_files: FileField（証憑添付等）用の一時ファイル管理（CLB 既定スキーマ）

CREATE TABLE IF NOT EXISTS app_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_name TEXT NOT NULL UNIQUE,
    name TEXT,
    hash TEXT NOT NULL,
    salt TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS temporary_files (
    guid TEXT PRIMARY KEY,
    created_date_time DATETIME NOT NULL
);
