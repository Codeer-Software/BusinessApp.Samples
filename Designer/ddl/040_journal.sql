-- 040_journal.sql — 仕訳（会計コアの心臓）＋前提マスタの器（AccountingSQLite）
-- 設計: docs/04_会計ドメイン設計.md §3 / decisions/0002(ヘッダ+貸借区分付き明細行) / 0003(明示税行)
-- departments/partners/projects は仕訳行の FK 先として器を先行作成（画面はフェーズ B-1）。
-- 注意: 日付列は DATE、日時列は DATETIME で宣言（TEXT 禁止 — Project.md 知見）。

CREATE TABLE IF NOT EXISTS departments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    display_order INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS partners (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    kana TEXT,
    is_customer INTEGER NOT NULL DEFAULT 0,
    is_supplier INTEGER NOT NULL DEFAULT 0,
    invoice_reg_no TEXT,                  -- 適格請求書発行事業者登録番号 (T+13桁)
    is_tax_exempt INTEGER NOT NULL DEFAULT 0,  -- 1=免税事業者（インボイス経過措置対象）
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS projects (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    partner_id INTEGER REFERENCES partners(id),
    project_type TEXT NOT NULL DEFAULT 'contract',  -- contract(受託) / ses / saas / internal
    status TEXT NOT NULL DEFAULT 'active',          -- active / completed / suspended
    is_active INTEGER NOT NULL DEFAULT 1
);

-- 伝票ヘッダ
CREATE TABLE IF NOT EXISTS journal_entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    journal_no INTEGER,                   -- 年度内連番（確定時に採番。draft は NULL 可）
    fiscal_year_id INTEGER NOT NULL REFERENCES fiscal_years(id),
    entry_date DATE NOT NULL,
    entry_type TEXT NOT NULL DEFAULT 'transfer',  -- transfer/receipt/payment/expense/auto/adjust
    description TEXT,
    status TEXT NOT NULL DEFAULT 'draft', -- draft / posted
    source_type TEXT,                     -- 連動元 (expense/invoice/depreciation/closing...) 手入力は NULL
    source_id INTEGER,
    creator INTEGER,                      -- CLB 予約名 Creator (AppUser.id 自動セット)
    created_at DATETIME,                  -- CLB 予約名 CreatedAt
    updater INTEGER,
    updated_at DATETIME
);

-- 仕訳明細行（1行1側: dc + 正の金額）
CREATE TABLE IF NOT EXISTS journal_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    journal_entry_id INTEGER NOT NULL REFERENCES journal_entries(id),
    line_no INTEGER NOT NULL,
    dc TEXT NOT NULL,                     -- D(借方) / C(貸方)
    account_id INTEGER NOT NULL REFERENCES accounts(id),
    sub_account_id INTEGER REFERENCES sub_accounts(id),
    department_id INTEGER REFERENCES departments(id),
    project_id INTEGER REFERENCES projects(id),
    amount INTEGER NOT NULL,              -- 円（税抜経理の本体額。税は別行）
    tax_category_id INTEGER REFERENCES tax_categories(id),
    tax_input_mode TEXT,                  -- inclusive(内税) / exclusive(外税) / none
    input_amount INTEGER,                 -- ユーザー入力額（内税なら税込）。監査・再計算用
    is_tax_line INTEGER NOT NULL DEFAULT 0,  -- 1=システム生成の消費税行
    parent_line_no INTEGER,               -- 税行が紐づく元行の line_no
    description TEXT
);

CREATE INDEX IF NOT EXISTS idx_journal_entries_date ON journal_entries(entry_date);
CREATE INDEX IF NOT EXISTS idx_journal_entries_year_no ON journal_entries(fiscal_year_id, journal_no);
CREATE INDEX IF NOT EXISTS idx_journal_lines_entry ON journal_lines(journal_entry_id);
CREATE INDEX IF NOT EXISTS idx_journal_lines_account ON journal_lines(account_id);

-- ---- seed: 部門（ペルソナ docs/02 §3。2026-07-09 組織再編で管理部→総務部に改称 → docs/decisions/0018） ----
-- 全社共通(00) はユーザーを置かない費用集計軸（家賃・全社ライセンス等の共通費の帰属先）
INSERT OR IGNORE INTO departments (code, name, display_order) VALUES
    ('00', '全社共通', 0),
    ('10', '総務部', 10),
    ('20', '営業部', 20),
    ('31', '開発1部', 31),
    ('32', '開発2部', 32),
    ('40', 'SaaS事業部', 40);
