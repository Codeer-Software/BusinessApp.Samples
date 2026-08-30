-- 自社の法人番号に、取引先と同じ CHECK を置く（qa/02 R26-19）。
-- 桁と字種だけを見る。検査用数字はアプリ側（CompanyProfileSubmitGate → CorporateNumber）。
--
-- **CHECK の追加は作り直しでしか行えない**（migrations/README のレシピ）。
--
-- **適用前の確認**: 既存の行が新しい CHECK に反していると、書き戻しで失敗して丸ごと巻き戻る。
--   SELECT COUNT(*) FROM company_profile
--    WHERE corporate_number IS NOT NULL
--      AND corporate_number NOT GLOB '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]';

PRAGMA defer_foreign_keys = ON;

CREATE TABLE company_profile_rebuild AS SELECT * FROM company_profile;
CREATE TABLE company_profile_rebuild_seq AS SELECT seq FROM sqlite_sequence WHERE name = 'company_profile';
DROP TABLE company_profile;

CREATE TABLE company_profile (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT CHECK (id = 1),

    name                        TEXT NOT NULL,
    name_kana                   TEXT,
    -- 法人番号（13 桁）。**取引先の同じ列と同じ CHECK を置く**（2026-08-31。qa/02 R26-19）。
    -- 検査用数字はアプリ側（CompanyProfileSubmitGate → CorporateNumber）。DB は桁と字種だけを見る。
    -- 揃えていないと、**同じ値が入る 2 つの列で「DB が受け取る範囲」が違う**ことになる。
    corporate_number            TEXT CHECK (corporate_number IS NULL OR corporate_number GLOB '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]'),
    representative_name         TEXT,
    postal_code                 TEXT,
    address                     TEXT,
    phone_number                TEXT,

    -- 決算月。会計年度の生成に使う。
    fiscal_year_end_month       INTEGER NOT NULL CHECK (fiscal_year_end_month BETWEEN 1 AND 12),

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0029）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0
);

INSERT INTO company_profile (id, name, name_kana, corporate_number, representative_name,
                             postal_code, address, phone_number, fiscal_year_end_month,
                             created_at, updated_at, creator, updater, optimistic_locking)
    SELECT id, name, name_kana, corporate_number, representative_name,
           postal_code, address, phone_number, fiscal_year_end_month,
           created_at, updated_at, creator, updater, optimistic_locking
    FROM company_profile_rebuild;
DROP TABLE company_profile_rebuild;
UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM company_profile_rebuild_seq))
 WHERE name = 'company_profile' AND EXISTS (SELECT 1 FROM company_profile_rebuild_seq);
INSERT INTO sqlite_sequence (name, seq)
    SELECT 'company_profile', seq FROM company_profile_rebuild_seq
    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'company_profile');
DROP TABLE company_profile_rebuild_seq;
