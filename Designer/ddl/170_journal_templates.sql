-- 170_journal_templates.sql — 定型仕訳（仕訳辞書）D-4
-- 毎月の家賃・給与・社会保険料など「科目は同じで金額だけ変わる」繰り返し仕訳のテンプレート。
-- 「起票」で draft の振替伝票を生成し、金額を確認・修正して確定する運用
-- （税行はテンプレートに持たず、伝票確定時の RegenerateTaxLines に任せる）。

CREATE TABLE IF NOT EXISTS journal_templates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    memo TEXT,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS journal_template_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    template_id INTEGER REFERENCES journal_templates(id),  -- 親FK（CLB 多段生成のため NOT NULL 禁止）
    line_no INTEGER,
    dc TEXT NOT NULL DEFAULT 'D',          -- D(借方) / C(貸方)
    account_id INTEGER REFERENCES accounts(id),
    amount INTEGER,                         -- 既定金額（税込入力額。起票後に伝票で修正可）
    tax_category_id INTEGER REFERENCES tax_categories(id),
    tax_input_mode TEXT,                    -- inclusive / exclusive / none
    description TEXT
);

CREATE INDEX IF NOT EXISTS idx_jtl_template ON journal_template_lines(template_id);

-- ---- seed（ペルソナの毎月の定型3種） ----
INSERT OR IGNORE INTO journal_templates (id, code, name, memo) VALUES
    (1, 'T01', '事務所家賃の支払', '毎月末の口座振替。金額固定'),
    (2, 'T02', '給与の支払', '毎月25日。金額は給与計算の結果で修正する'),
    (3, 'T03', '社会保険料の納付', '毎月末の口座振替。会社負担分＋従業員預り分');

INSERT OR IGNORE INTO journal_template_lines (id, template_id, line_no, dc, account_id, amount, tax_category_id, tax_input_mode, description) VALUES
    (1, 1, 1, 'D', (SELECT id FROM accounts WHERE code = '6200'), 165000,
        (SELECT id FROM tax_categories WHERE code = 'PUR_10'), 'inclusive', '事務所家賃'),
    (2, 1, 2, 'C', (SELECT id FROM accounts WHERE code = '1020'), 165000, NULL, 'none', '家賃口座振替'),
    (3, 2, 1, 'D', (SELECT id FROM accounts WHERE code = '6010'), 3000000,
        (SELECT id FROM tax_categories WHERE code = 'NON_TAXABLE'), 'none', '給与総額'),
    (4, 2, 2, 'C', (SELECT id FROM accounts WHERE code = '2050'), 450000, NULL, 'none', '源泉所得税・社会保険料の預り'),
    (5, 2, 3, 'C', (SELECT id FROM accounts WHERE code = '1020'), 2550000, NULL, 'none', '給与振込'),
    (6, 3, 1, 'D', (SELECT id FROM accounts WHERE code = '6030'), 280000,
        (SELECT id FROM tax_categories WHERE code = 'NON_TAXABLE'), 'none', '社会保険料 会社負担分'),
    (7, 3, 2, 'D', (SELECT id FROM accounts WHERE code = '2050'), 140000, NULL, 'none', '従業員預り分の充当'),
    (8, 3, 3, 'C', (SELECT id FROM accounts WHERE code = '1020'), 420000, NULL, 'none', '社会保険料 口座振替');
