-- 認証部品の app_users に、本プロジェクトの役割の列を足す（ADR-0034・ADR-0032）。
--
-- **CREATE TABLE IF NOT EXISTS を先に書く理由。** このテーブルは本プロジェクトが作ったものではなく、
-- baseline（凍結した Designer/ddl のコピー）には入っていない。
-- **稼働 DB には既にある**ので CREATE は何もしないが、
-- **同値検査は baseline から再生する**（MigrationEquivalenceTests）ので、そこには無い。
-- 無ければ作ってから列を足せば、どちらの経路でも正典（ddl/007_auth.sql）と同じ姿になる。
--
-- 本体の列（id / user_name / name / hash / salt）と索引は CLB のものである。
-- **稼働 DB から写した現物**であって、本プロジェクトが決めた形ではない。
--
-- **既定値の向き**: can_access_app だけ真、権限は偽と NULL。
-- 既にいる利用者を締め出さず、かつ**権限は誰にも与えない**（安全側）。
-- admin に is_sysadmin を立てるのは初期データの仕事である（ADR-0034 の帰結）。

-- **役割の付与はここに書かない。** migrations/README は「データ行の同値の網が入るまで
-- DML マイグレーションは書かない」と定めている。そのうえ**新しい DB では空振りする**——
-- `admin` は CLB がサーバ起動時に作るので、マイグレーションを当てる時点ではまだ居ない。
-- 最初の 1 人に `is_sysadmin` を立てるのは**導入の手順**である（Designer/seed/README）。

CREATE TABLE IF NOT EXISTS app_users (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    user_name                   TEXT NOT NULL UNIQUE,
    name                        TEXT,
    hash                        TEXT NOT NULL,
    salt                        TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_app_users_name ON app_users (name);

ALTER TABLE app_users ADD COLUMN can_access_app  INTEGER NOT NULL DEFAULT 1 CHECK (can_access_app IN (0, 1));
ALTER TABLE app_users ADD COLUMN is_sysadmin     INTEGER NOT NULL DEFAULT 0 CHECK (is_sysadmin IN (0, 1));
ALTER TABLE app_users ADD COLUMN accounting_role TEXT CHECK (accounting_role IS NULL OR accounting_role IN ('viewer', 'staff', 'manager'));
ALTER TABLE app_users ADD COLUMN partner_role    TEXT CHECK (partner_role IS NULL OR partner_role IN ('viewer', 'editor'));
