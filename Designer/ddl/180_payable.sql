-- 180_payable.sql — 買掛・支払管理（D-6 / 購買側の業務フロー。B-4 販売側の鏡像）
-- 仕入先請求書の受領 → 未払計上（D 費用+税 / C 買掛金2000）→ 支払予定表 → 支払登録（D 買掛金 / C 預金）。
-- 経費精算の未払金(2020)とは使い分け: 社員立替の精算=未払金 / 仕入先への事業上の債務（外注費等）=買掛金。

CREATE TABLE IF NOT EXISTS vendor_invoices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    invoice_no TEXT,                        -- 仕入先の請求書番号
    partner_id INTEGER REFERENCES partners(id),
    received_date DATE,                     -- 受領日
    invoice_date DATE,                      -- 請求日（未払計上の仕訳日付）
    due_date DATE,                          -- 支払期限
    expense_account_id INTEGER REFERENCES accounts(id),   -- 費用科目（外注費 5000 等）
    tax_category_id INTEGER REFERENCES tax_categories(id),
    amount INTEGER,                         -- 税込請求額
    description TEXT,
    status TEXT NOT NULL DEFAULT 'received',   -- received(受領) / accrued(未払計上済) / paid(支払済)
    accrual_entry_id INTEGER REFERENCES journal_entries(id),  -- 未払計上仕訳リンク
    payment_entry_id INTEGER REFERENCES journal_entries(id),  -- 支払仕訳リンク
    paid_date DATE,
    bank_account_id INTEGER REFERENCES bank_accounts(id)      -- 支払口座
);

CREATE INDEX IF NOT EXISTS idx_vendor_invoices_status ON vendor_invoices(status);
CREATE INDEX IF NOT EXISTS idx_vendor_invoices_due ON vendor_invoices(due_date);

-- ---- seed ----
-- 仕入先: オフショア開発パートナー（新規）。C001 は得意先兼仕入先に更新
INSERT OR IGNORE INTO partners (id, code, name, kana, is_customer, is_supplier, is_active) VALUES
    (2, 'C002', '株式会社ベガソフト', 'ベガソフト', 0, 1, 1);
UPDATE partners SET is_supplier = 1 WHERE code = 'C001';

INSERT OR IGNORE INTO vendor_invoices
    (id, invoice_no, partner_id, received_date, invoice_date, due_date,
     expense_account_id, tax_category_id, amount, description, status) VALUES
    (1, 'VG-2026-071', (SELECT id FROM partners WHERE code = 'C002'),
     '2026-07-03', '2026-06-30', '2026-08-31',
     (SELECT id FROM accounts WHERE code = '5000'),
     (SELECT id FROM tax_categories WHERE code = 'PUR_10'),
     550000, '基幹システム改修 外注費 6月分', 'received'),
    (2, 'AL-7005', (SELECT id FROM partners WHERE code = 'C001'),
     '2026-07-05', '2026-07-01', '2026-07-31',
     (SELECT id FROM accounts WHERE code = '6140'),
     (SELECT id FROM tax_categories WHERE code = 'PUR_10'),
     33000, '開発用モニター購入', 'received');
