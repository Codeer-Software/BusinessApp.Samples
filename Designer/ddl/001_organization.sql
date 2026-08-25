-- 001 事業所情報（ADR-0019 ② 会計設定）
--
-- 単一法人に徹する（ADR-0005）ので 1 行しか持たない。
-- CHECK (id = 1) で 2 行目の INSERT を DB が拒む。

CREATE TABLE company_profile (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT CHECK (id = 1),

    name                        TEXT NOT NULL,
    name_kana                   TEXT,
    corporate_number            TEXT,                       -- 法人番号 13 桁
    representative_name         TEXT,
    postal_code                 TEXT,
    address                     TEXT,
    phone_number                TEXT,

    -- 決算月。会計年度の生成に使う。
    fiscal_year_end_month       INTEGER NOT NULL CHECK (fiscal_year_end_month BETWEEN 1 AND 12),

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0019）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0
);
