-- 130_budget.sql — 予算管理（部門×科目×月、B-7、AccountingSQLite）
-- 規律: FK 列に NOT NULL 禁止 / 金額=INTEGER 円 / 閾値はマスタ化（ハードコード禁止）

CREATE TABLE IF NOT EXISTS budget_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    fiscal_year_id INTEGER REFERENCES fiscal_years(id),
    department_id INTEGER REFERENCES departments(id),
    account_id INTEGER REFERENCES accounts(id),
    period_no INTEGER,              -- 1〜12（期首月=1。fiscal_periods.period_no と対応）
    amount INTEGER,                 -- 月次予算額（税抜）
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);
CREATE INDEX IF NOT EXISTS idx_budget_lines_key ON budget_lines(fiscal_year_id, department_id, account_id, period_no);

-- 予算消化の警告率（予実対比の ⚠ 判定に使用）
INSERT INTO system_thresholds (code, name, amount, valid_from, valid_to)
SELECT 'BUDGET_ALERT_RATE', '予算消化の警告率(%)', 80, NULL, NULL
WHERE NOT EXISTS (SELECT 1 FROM system_thresholds WHERE code = 'BUDGET_ALERT_RATE');
