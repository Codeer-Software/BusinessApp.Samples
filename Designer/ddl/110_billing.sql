-- 110_billing.sql — 請求・入金（B-4、AccountingSQLite）
-- 設計: docs/08_請求入金設計.md / 売上計上=検収基準 (decisions/0008)
-- 規律: FK 列に NOT NULL 禁止（CLB 後埋め）/ 日付=DATE / 金額=INTEGER 円

CREATE TABLE IF NOT EXISTS quotes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    quote_no TEXT,                                   -- Q-{yy}-{seq} スクリプト採番
    partner_id INTEGER REFERENCES partners(id),
    project_id INTEGER REFERENCES projects(id),
    title TEXT,
    issue_date DATE,
    valid_until DATE,
    status TEXT,                                     -- draft / sent / accepted / rejected
    note TEXT,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);

CREATE TABLE IF NOT EXISTS quote_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    quote_id INTEGER REFERENCES quotes(id),
    line_no INTEGER,
    description TEXT,
    qty INTEGER,
    unit_price INTEGER,
    amount INTEGER,                                  -- qty × unit_price（税抜）
    tax_category_id INTEGER REFERENCES tax_categories(id)
);

CREATE TABLE IF NOT EXISTS sales_orders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    order_no TEXT,                                   -- SO-{yy}-{seq}
    quote_id INTEGER REFERENCES quotes(id),          -- NULL=見積なし受注（SES 更新等）
    partner_id INTEGER REFERENCES partners(id),
    project_id INTEGER REFERENCES projects(id),
    title TEXT,
    order_date DATE,
    status TEXT,                                     -- open / closed
    note TEXT,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);

CREATE TABLE IF NOT EXISTS sales_order_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sales_order_id INTEGER REFERENCES sales_orders(id),
    line_no INTEGER,
    description TEXT,
    qty INTEGER,
    unit_price INTEGER,
    amount INTEGER,
    tax_category_id INTEGER REFERENCES tax_categories(id)
);

CREATE TABLE IF NOT EXISTS acceptances (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    acceptance_no TEXT,                              -- A-{yy}-{seq}
    sales_order_id INTEGER REFERENCES sales_orders(id),
    acceptance_date DATE,
    amount INTEGER,                                  -- 税抜合計
    tax_amount INTEGER,                              -- 消費税額
    status TEXT,                                     -- draft / confirmed（confirmed で売上仕訳）
    note TEXT,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);

CREATE TABLE IF NOT EXISTS invoices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_no TEXT,                                 -- INV-{yy}-{seq}
    partner_id INTEGER REFERENCES partners(id),
    project_id INTEGER REFERENCES projects(id),
    acceptance_id INTEGER REFERENCES acceptances(id),-- NULL=手動/定期
    title TEXT,
    issue_date DATE,
    due_date DATE,
    amount INTEGER,                                  -- 税抜合計
    tax_amount INTEGER,
    status TEXT,                                     -- issued / partial / paid / void
    invoice_source TEXT,                             -- manual / acceptance / recurring(B-5)
    note TEXT,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);

CREATE TABLE IF NOT EXISTS invoice_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_id INTEGER REFERENCES invoices(id),
    line_no INTEGER,
    description TEXT,
    qty INTEGER,
    unit_price INTEGER,
    amount INTEGER,
    tax_category_id INTEGER REFERENCES tax_categories(id)
);

CREATE TABLE IF NOT EXISTS receipts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    receipt_date DATE,
    invoice_id INTEGER REFERENCES invoices(id),
    amount INTEGER,                                  -- 入金額（税込）
    method TEXT,                                     -- bank / cash / offset
    note TEXT,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);

CREATE INDEX IF NOT EXISTS idx_quote_lines_quote ON quote_lines(quote_id);
CREATE INDEX IF NOT EXISTS idx_so_lines_so ON sales_order_lines(sales_order_id);
CREATE INDEX IF NOT EXISTS idx_acceptances_so ON acceptances(sales_order_id);
CREATE INDEX IF NOT EXISTS idx_invoice_lines_inv ON invoice_lines(invoice_id);
CREATE INDEX IF NOT EXISTS idx_receipts_inv ON receipts(invoice_id);
CREATE INDEX IF NOT EXISTS idx_invoices_status ON invoices(status);
