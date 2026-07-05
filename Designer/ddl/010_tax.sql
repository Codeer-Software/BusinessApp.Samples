-- 010_tax.sql — 税マスタ（AccountingSQLite / accounting_v1.db）
-- 設計: docs/04_会計ドメイン設計.md §4 / docs/decisions/0003
-- 制度値の根拠: docs/research/2026-07_税制・会計制度リサーチ.md（令和8年度税制改正反映）
-- 税率・区分・閾値はすべて期間付きマスタ。ハードコード禁止の中核。

CREATE TABLE IF NOT EXISTS tax_rates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    rate_percent REAL NOT NULL,          -- 10 / 8 （将来 1 等の追加もレコードで対応）
    valid_from DATE NOT NULL,            -- ISO 日付
    valid_to DATE,                       -- NULL = 無期限
    display_order INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS tax_categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    -- taxable_sales / taxable_purchase / exempt_sales(非課税売上) / exempt_purchase(非課税仕入)
    -- / non_taxable(不課税) / export_exempt(免税売上) / out_of_scope(対象外)
    taxation_type TEXT NOT NULL,
    tax_rate_id INTEGER REFERENCES tax_rates(id),          -- NULL = 税率なし（不課税等）
    uses_transition_deduction INTEGER NOT NULL DEFAULT 0,  -- 1 = インボイス経過措置（免税事業者からの仕入）
    display_order INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1
);

-- インボイス経過措置の控除割合（令和8年度改正: 8・7・5・3割の4段階）
CREATE TABLE IF NOT EXISTS invoice_transition_rates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    valid_from DATE NOT NULL,
    valid_to DATE NOT NULL,
    rate_percent INTEGER NOT NULL        -- 控除できる割合(%)
);

-- 制度閾値（金額は「未満」判定で使う。少額資産 10万/20万/40万・中小特例の年間上限300万）
CREATE TABLE IF NOT EXISTS system_thresholds (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL,                  -- SMALL_ASSET_EXPENSE / LUMP_SUM_ASSET / SME_IMMEDIATE / SME_ANNUAL_CAP
    name TEXT NOT NULL,
    amount INTEGER NOT NULL,             -- 円
    valid_from DATE,                     -- NULL = 期間の定めなし
    valid_to DATE
);

-- ---- seed ----
INSERT OR IGNORE INTO tax_rates (code, name, rate_percent, valid_from, valid_to, display_order) VALUES
    ('STD10', '標準税率 10%', 10, '2019-10-01', NULL, 10),
    ('RED8',  '軽減税率 8%',  8,  '2019-10-01', NULL, 20);

INSERT OR IGNORE INTO tax_categories (code, name, taxation_type, tax_rate_id, uses_transition_deduction, display_order) VALUES
    ('SALES_10',     '課税売上 10%',                'taxable_sales',    (SELECT id FROM tax_rates WHERE code='STD10'), 0, 10),
    ('SALES_8',      '課税売上 8%(軽減)',           'taxable_sales',    (SELECT id FROM tax_rates WHERE code='RED8'),  0, 20),
    ('SALES_EXEMPT', '非課税売上',                  'exempt_sales',     NULL, 0, 30),
    ('SALES_EXPORT', '免税売上(輸出等)',            'export_exempt',    NULL, 0, 40),
    ('PUR_10',       '課税仕入 10%',                'taxable_purchase', (SELECT id FROM tax_rates WHERE code='STD10'), 0, 50),
    ('PUR_8',        '課税仕入 8%(軽減)',           'taxable_purchase', (SELECT id FROM tax_rates WHERE code='RED8'),  0, 60),
    ('PUR_10_TR',    '課税仕入 10%(経過措置)',      'taxable_purchase', (SELECT id FROM tax_rates WHERE code='STD10'), 1, 70),
    ('PUR_8_TR',     '課税仕入 8%(経過措置)',       'taxable_purchase', (SELECT id FROM tax_rates WHERE code='RED8'),  1, 80),
    ('PUR_EXEMPT',   '非課税仕入',                  'exempt_purchase',  NULL, 0, 90),
    ('NON_TAXABLE',  '不課税',                      'non_taxable',      NULL, 0, 100),
    ('OUT_OF_SCOPE', '対象外',                      'out_of_scope',     NULL, 0, 110);

INSERT OR IGNORE INTO invoice_transition_rates (id, valid_from, valid_to, rate_percent) VALUES
    (1, '2023-10-01', '2026-09-30', 80),
    (2, '2026-10-01', '2028-09-30', 70),
    (3, '2028-10-01', '2030-09-30', 50),
    (4, '2030-10-01', '2031-09-30', 30);

INSERT OR IGNORE INTO system_thresholds (id, code, name, amount, valid_from, valid_to) VALUES
    (1, 'SMALL_ASSET_EXPENSE', '少額（全額損金）上限未満', 100000, NULL, NULL),
    (2, 'LUMP_SUM_ASSET',      '一括償却資産 上限未満',    200000, NULL, NULL),
    (3, 'SME_IMMEDIATE',       '中小企業者等の少額特例 上限未満', 300000, NULL, '2026-03-31'),
    (4, 'SME_IMMEDIATE',       '中小企業者等の少額特例 上限未満(令和8年度改正)', 400000, '2026-04-01', '2029-03-31'),
    (5, 'SME_ANNUAL_CAP',      '中小特例の年間合計上限',   3000000, NULL, NULL);
