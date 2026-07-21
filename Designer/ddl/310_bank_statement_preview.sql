-- 310: 銀行明細取込のプレビュー分離（ISSUE-0003 v3）
-- 未確定の作業中データ（プレビュー）を本番 bank_statement_lines から分離する。
-- 以後 bank_statement_lines の status は pending / journalized / excluded の3値。
CREATE TABLE IF NOT EXISTS bank_statement_preview (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    bank_account_id INTEGER REFERENCES bank_accounts(id),
    line_date DATE,
    description TEXT,
    amount_out INTEGER,
    amount_in INTEGER,
    balance INTEGER,
    dedup_key TEXT,
    imported_at DATETIME
);
-- 移行: 旧方式の作業中プレビュー行は破棄する（未登録データのため安全。残数は移行時に確認）
DELETE FROM bank_statement_lines WHERE status = 'preview';
