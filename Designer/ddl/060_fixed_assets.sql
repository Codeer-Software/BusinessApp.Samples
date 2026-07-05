-- 060_fixed_assets.sql — 固定資産台帳（AccountingSQLite / accounting_v1.db）
-- 設計: docs/04 §7。償却累計は持たず仕訳（source_type='depreciation', source_id=資産id）から導出。
-- 償却方法: straight_line(定額) / declining_200(200%定率) / lump_sum_3yr(一括3年) / immediate(即時) / none(非償却)

CREATE TABLE IF NOT EXISTS fixed_assets (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    asset_account_id INTEGER NOT NULL REFERENCES accounts(id),
    department_id INTEGER REFERENCES departments(id),
    acquisition_date DATE NOT NULL,
    acquisition_cost INTEGER NOT NULL,   -- 税抜取得価額（円）
    depreciation_method TEXT NOT NULL DEFAULT 'straight_line',
    useful_life INTEGER,                 -- 耐用年数（年）。immediate/none は NULL 可
    status TEXT NOT NULL DEFAULT 'in_use',  -- in_use / retired / sold
    retired_date DATE,
    memo TEXT
);

CREATE INDEX IF NOT EXISTS idx_fixed_assets_account ON fixed_assets(asset_account_id);
