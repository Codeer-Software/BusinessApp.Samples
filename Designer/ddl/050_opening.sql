-- 050_opening.sql — 期首残高（BusinessAppSQLite / business-app_v1.db）
-- 設計: docs/04 §6 / decisions/0006（年次繰越=翌期 opening_balances 生成方式）
-- balance は符号付き（借方残=正・貸方残=負）。年度内の合計が 0 で貸借一致。

CREATE TABLE IF NOT EXISTS opening_balances (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    fiscal_year_id INTEGER NOT NULL REFERENCES fiscal_years(id),
    account_id INTEGER NOT NULL REFERENCES accounts(id),
    sub_account_id INTEGER REFERENCES sub_accounts(id),
    department_id INTEGER REFERENCES departments(id),
    balance INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_opening_balances_year ON opening_balances(fiscal_year_id);
