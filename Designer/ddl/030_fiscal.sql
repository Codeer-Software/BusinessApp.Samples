-- 030_fiscal.sql — 会計期間（AccountingSQLite / accounting_v1.db）
-- 設計: docs/04_会計ドメイン設計.md §1
-- seed: ペルソナ（docs/02）どおり 第17期(2025-04〜2026-03, 締め済み)・第18期(2026-04〜, 進行中)。
--       第18期は 4月・5月を月次締め済み、6月以降 open（A-4 の締め済みガード検証にも使う）。

CREATE TABLE IF NOT EXISTS fiscal_years (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    status TEXT NOT NULL DEFAULT 'open'   -- open / closed
);

CREATE TABLE IF NOT EXISTS fiscal_periods (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    fiscal_year_id INTEGER NOT NULL REFERENCES fiscal_years(id),
    period_no INTEGER NOT NULL,           -- 1〜12（期首月=1）
    start_date DATE NOT NULL,
    end_date DATE NOT NULL,
    status TEXT NOT NULL DEFAULT 'open',  -- open / closed
    UNIQUE(fiscal_year_id, period_no)
);

CREATE INDEX IF NOT EXISTS idx_fiscal_periods_year ON fiscal_periods(fiscal_year_id);

-- ---- seed ----
INSERT OR IGNORE INTO fiscal_years (id, name, start_date, end_date, status) VALUES
    (1, '第17期', '2025-04-01', '2026-03-31', 'closed'),
    (2, '第18期', '2026-04-01', '2027-03-31', 'open');

INSERT OR IGNORE INTO fiscal_periods (id, fiscal_year_id, period_no, start_date, end_date, status) VALUES
    (1,  1, 1,  '2025-04-01', '2025-04-30', 'closed'),
    (2,  1, 2,  '2025-05-01', '2025-05-31', 'closed'),
    (3,  1, 3,  '2025-06-01', '2025-06-30', 'closed'),
    (4,  1, 4,  '2025-07-01', '2025-07-31', 'closed'),
    (5,  1, 5,  '2025-08-01', '2025-08-31', 'closed'),
    (6,  1, 6,  '2025-09-01', '2025-09-30', 'closed'),
    (7,  1, 7,  '2025-10-01', '2025-10-31', 'closed'),
    (8,  1, 8,  '2025-11-01', '2025-11-30', 'closed'),
    (9,  1, 9,  '2025-12-01', '2025-12-31', 'closed'),
    (10, 1, 10, '2026-01-01', '2026-01-31', 'closed'),
    (11, 1, 11, '2026-02-01', '2026-02-28', 'closed'),
    (12, 1, 12, '2026-03-01', '2026-03-31', 'closed'),
    (13, 2, 1,  '2026-04-01', '2026-04-30', 'closed'),
    (14, 2, 2,  '2026-05-01', '2026-05-31', 'closed'),
    (15, 2, 3,  '2026-06-01', '2026-06-30', 'open'),
    (16, 2, 4,  '2026-07-01', '2026-07-31', 'open'),
    (17, 2, 5,  '2026-08-01', '2026-08-31', 'open'),
    (18, 2, 6,  '2026-09-01', '2026-09-30', 'open'),
    (19, 2, 7,  '2026-10-01', '2026-10-31', 'open'),
    (20, 2, 8,  '2026-11-01', '2026-11-30', 'open'),
    (21, 2, 9,  '2026-12-01', '2026-12-31', 'open'),
    (22, 2, 10, '2027-01-01', '2027-01-31', 'open'),
    (23, 2, 11, '2027-02-01', '2027-02-28', 'open'),
    (24, 2, 12, '2027-03-01', '2027-03-31', 'open');
