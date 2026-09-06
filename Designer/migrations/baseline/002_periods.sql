-- 002 会計年度・会計期間（ADR-0006 ② 会計設定）
--
-- 「マスタ」ではなく状態を持つ台帳として扱う。締め状態（open / closed）が動くため、
-- 静的な参照データとは性質が違う（docs/10 §7）。
--
-- 日付は DATE で宣言する。TEXT にすると CLB が DateOnly を MM/dd/yyyy 文字列にして
-- 比較するため、年跨ぎの範囲検索が壊れる（DatabaseGuidelines）。

CREATE TABLE fiscal_years (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    code                        TEXT NOT NULL UNIQUE,       -- 例 "FY18"
    label                       TEXT NOT NULL,              -- 例 「第 18 期（2026 年度）」
    start_date                  DATE NOT NULL,
    end_date                    DATE NOT NULL,
    status                      TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),

    -- 優良な電子帳簿の適用開始日。課税期間の初日から全帳簿で要件を満たす必要があるため
    -- （法 8 ④・令 2）、start_date と一致しない場合はアプリが警告する。
    premium_ledger_from         DATE,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0006）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    CHECK (start_date <= end_date)
);

CREATE TABLE accounting_periods (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    fiscal_year_id              INTEGER NOT NULL REFERENCES fiscal_years(id),
    start_date                  DATE NOT NULL,              -- 月初日
    end_date                    DATE NOT NULL,              -- 月末日
    status                      TEXT NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0006）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    CHECK (start_date <= end_date),
    UNIQUE (fiscal_year_id, start_date)
);

-- 計上日から期間を引く経路。仕訳を 1 本入れるたびに通る。
CREATE INDEX ix_accounting_periods_range ON accounting_periods (start_date, end_date);
