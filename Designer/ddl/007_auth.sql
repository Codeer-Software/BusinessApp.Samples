-- 007 認証部品の利用者アカウント（docs/12_マスタ台帳）
--
-- **このテーブルは本プロジェクトが作ったものではない。** Codeer.LowCode.Blazor の Cookie 認証が
-- 使うユーザーテーブルで、形は CLB 仕様リファレンス `_specs/Authentication.md` が決めている
-- （`PasswordCheckUserTableInfo` が列名を指す）。ログインは
-- `WHERE user_name = @Id` の 1 件を引いてハッシュを照合するだけである。
--
-- **それでも正典に置く理由。** 役割の列（下の 4 本）は本プロジェクトが足すもので、
-- **`app_users` に間借りする**（ADR-0034）。CLB の権限条件は
-- **ログインユーザーのレコードの列しか参照できない**（関連テーブルを辿れない。qa/01 F-21）ので、
-- 別テーブルに逃がすことができない。正典が知らないままにすると、
-- **間借りの列が欠けていても・型が違っても、誰も気づかない**——マイグレーションの同値検査も
-- 稼働 DB の検証も「管轄外」として素通りする。
--
-- **本体の列（id / user_name / name / hash / salt と索引）は CLB のものなので変えない。**
-- ここに書いてあるのは稼働 DB から写した現物である（2026-08-31 実測）。

CREATE TABLE app_users (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    user_name                   TEXT NOT NULL UNIQUE,
    name                        TEXT,
    hash                        TEXT NOT NULL,
    salt                        TEXT NOT NULL,

    -- ここから下が本プロジェクトの間借り（ADR-0034・ADR-0032）。
    -- 新しい列は末尾に置く（migrations/README の規約。ALTER TABLE ADD COLUMN と同値になる）。

    -- **このアプリに入れるか。** `app.clprj` の「アプリ全体のアクセス条件」がこれを見る
    -- （CLB の認可 5 階層の最上位。画面ごとの条件に足す必要が無く、API 経由も閉じる）。
    -- 退職・休職はこれを偽にして表す（開発者との往復。2026-08-30）。
    -- **`is_active` と名づけない**——マスタの `is_active` は「入力候補に出すか」で意味が違う。
    -- **既定は真**。CLB はユーザーテーブルが空のとき `admin` を自動で入れるので、
    -- 偽を既定にすると、その `admin` が生まれた瞬間に締め出される。
    can_access_app              INTEGER NOT NULL DEFAULT 1 CHECK (can_access_app IN (0, 1)),

    -- 権限の軸は**機能（部品）ごとに直交**する（開発者の整理。2026-08-28。ADR-0034）。
    -- システム管理の権限を業務側の条件に混ぜない。
    is_sysadmin                 INTEGER NOT NULL DEFAULT 0 CHECK (is_sysadmin IN (0, 1)),

    -- **軸の中は階層**（経理責任者 ⊃ 経理担当 ⊃ 帳簿閲覧）。1 機能 1 列で、
    -- 「責任者かつ担当」のような無意味な組み合わせが構造的に作れない。
    -- 既定は NULL＝**何もできない**（安全側）。`viewer` の利用はフェーズ 4。
    accounting_role             TEXT CHECK (accounting_role IS NULL OR accounting_role IN ('viewer', 'staff', 'manager')),

    -- 取引先は独立の軸（編集 ⊃ 閲覧 ⊃ なし）。**会計の役割を見ない**——
    -- 見ると「取引先を触るのに会計権限を確かめる」ことになり、依存方向が逆になる。
    -- `viewer` の利用はフェーズ 4 だが、**CHECK には今から入れる**
    -- （SQLite の CHECK の変更はテーブルの作り直しになるため）。
    partner_role                TEXT CHECK (partner_role IS NULL OR partner_role IN ('viewer', 'editor'))
);

-- CLB が作る索引。写しである。
CREATE INDEX idx_app_users_name ON app_users (name);
