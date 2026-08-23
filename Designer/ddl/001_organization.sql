-- 001 事業所情報（ADR-0006 ② 会計設定）
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
    creator                     INTEGER REFERENCES app_users(id),
    updater                     INTEGER REFERENCES app_users(id),
    optimistic_locking          INTEGER NOT NULL DEFAULT 0
);
