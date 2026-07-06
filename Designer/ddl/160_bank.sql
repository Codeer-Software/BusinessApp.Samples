-- 160_bank.sql — 銀行・クレカ明細取込（D-2 / ADR-0012 ステージング＋仕訳リンク方式）
-- bank_accounts: 取込口座マスタ（口座⇔帳簿勘定科目の対応）
-- matching_rules: 摘要キーワード→相手勘定科目のマッチングルール（優先度順・ハードコード禁止の原則どおりマスタ化）
-- bank_statement_lines: 明細ステージング（journal_entry_id リンクで起票済み判定・残高照合・重複取込防止を導出）

CREATE TABLE IF NOT EXISTS bank_accounts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,                    -- 表示名（例: メインバンク 普通預金）
    account_kind TEXT NOT NULL DEFAULT 'bank',   -- bank / card
    ledger_account_id INTEGER REFERENCES accounts(id),  -- 帳簿側の勘定科目（普通預金1020 等）
    memo TEXT,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS matching_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    priority INTEGER NOT NULL DEFAULT 100, -- 小さいほど優先
    keyword TEXT NOT NULL,                 -- 摘要の部分一致キーワード
    direction TEXT NOT NULL DEFAULT 'any', -- in(入金のみ) / out(出金のみ) / any
    account_id INTEGER REFERENCES accounts(id),  -- 相手勘定科目
    memo TEXT,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS bank_statement_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    bank_account_id INTEGER REFERENCES bank_accounts(id),
    line_date DATE NOT NULL,
    description TEXT,
    amount_out INTEGER NOT NULL DEFAULT 0,
    amount_in INTEGER NOT NULL DEFAULT 0,
    balance INTEGER,                       -- 明細記載の残高（無い CSV は NULL）
    dedup_key TEXT NOT NULL,               -- 重複取込防止キー（日付|摘要|出金|入金|残高|同一内容連番）
    status TEXT NOT NULL DEFAULT 'pending',-- pending(未起票) / journalized(起票済) / excluded(対象外)
    suggested_account_id INTEGER REFERENCES accounts(id),  -- 相手科目の候補
    suggestion_source TEXT,                -- rule / ai / manual
    journal_entry_id INTEGER REFERENCES journal_entries(id),  -- 起票済み仕訳へのリンク（ADR-0012 の核）
    imported_at DATETIME
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_bsl_dedup ON bank_statement_lines(bank_account_id, dedup_key);
CREATE INDEX IF NOT EXISTS idx_bsl_status ON bank_statement_lines(status);
CREATE INDEX IF NOT EXISTS idx_bsl_date ON bank_statement_lines(line_date);

-- ---- seed ----
INSERT OR IGNORE INTO bank_accounts (id, code, name, account_kind, ledger_account_id, memo) VALUES
    (1, 'MB01', 'メインバンク 普通預金', 'bank',
     (SELECT id FROM accounts WHERE code = '1020'),
     'デモ用の主取引口座。CSV貼り付け取込のテスト対象');

INSERT OR IGNORE INTO matching_rules (id, priority, keyword, direction, account_id, memo) VALUES
    (1, 10, '振込手数料', 'out', (SELECT id FROM accounts WHERE code = '6210'), '支払手数料'),
    (2, 20, 'AWS',       'out', (SELECT id FROM accounts WHERE code = '6130'), 'クラウド利用料=通信費'),
    (3, 20, 'ﾄﾞｺﾓ',      'out', (SELECT id FROM accounts WHERE code = '6130'), '携帯電話'),
    (4, 30, 'JR',        'out', (SELECT id FROM accounts WHERE code = '6100'), '鉄道'),
    (5, 30, 'ETC',       'out', (SELECT id FROM accounts WHERE code = '6100'), '高速道路'),
    (6, 40, '家賃',      'out', (SELECT id FROM accounts WHERE code = '6200'), '事務所家賃'),
    (7, 40, 'ﾌﾄﾞｳｻﾝ',    'out', (SELECT id FROM accounts WHERE code = '6200'), '不動産会社への支払'),
    (8, 50, '給与',      'out', (SELECT id FROM accounts WHERE code = '6010'), '給与振込'),
    (9, 60, 'ﾃﾞﾝｷ',      'out', (SELECT id FROM accounts WHERE code = '6220'), '電力会社'),
    (10, 90, '振込',     'in',  (SELECT id FROM accounts WHERE code = '1100'), '入金の既定=売掛金回収');
