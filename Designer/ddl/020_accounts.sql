-- 020_accounts.sql — 勘定科目マスタ（BusinessAppSQLite / business-app_v1.db）
-- 設計: docs/04_会計ドメイン設計.md §2 / docs/decisions/0005（4桁コード・フラット＋表示区分）
-- プリセット科目は IT 受託ソフトハウス（docs/02_ペルソナ.md）向けの中小標準体系。
-- 注意: 010_tax.sql（tax_categories）適用後に実行すること。

CREATE TABLE IF NOT EXISTS account_categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    statement TEXT NOT NULL,             -- BS / PL
    section_order INTEGER NOT NULL       -- 帳票上の並び（BS/PL の組み上げ順）
);

CREATE TABLE IF NOT EXISTS accounts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,           -- 4桁（1000=資産〜9000=税金等の帯域制）
    name TEXT NOT NULL,
    kana TEXT,                           -- 検索用（後で整備）
    account_type TEXT NOT NULL,          -- asset / liability / equity / revenue / expense
    category_id INTEGER NOT NULL REFERENCES account_categories(id),
    dc_normal TEXT NOT NULL,             -- D / C（正残の側。貸倒引当金等の評価勘定は逆側）
    default_tax_category_id INTEGER REFERENCES tax_categories(id),  -- 仕訳入力時の既定税区分（下記 seed の NULL は 490 で OUT_OF_SCOPE に埋める。ADR-0052 以降 NULL は使わない）
    display_order INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS sub_accounts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL REFERENCES accounts(id),
    code TEXT NOT NULL,
    name TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    UNIQUE(account_id, code)
);

CREATE INDEX IF NOT EXISTS idx_accounts_category ON accounts(category_id);
CREATE INDEX IF NOT EXISTS idx_sub_accounts_account ON sub_accounts(account_id);

-- ---- seed: 表示区分 ----
INSERT OR IGNORE INTO account_categories (code, name, statement, section_order) VALUES
    ('CA',   '流動資産',           'BS', 10),
    ('FAT',  '有形固定資産',       'BS', 20),
    ('FAI',  '無形固定資産',       'BS', 21),
    ('FAO',  '投資その他の資産',   'BS', 22),
    ('CL',   '流動負債',           'BS', 30),
    ('LL',   '固定負債',           'BS', 31),
    ('EQC',  '資本金',             'BS', 40),
    ('EQS',  '資本剰余金',         'BS', 41),
    ('EQR',  '利益剰余金',         'BS', 42),
    ('REV',  '売上高',             'PL', 50),
    ('COGS', '売上原価',           'PL', 51),
    ('SGA',  '販売費及び一般管理費','PL', 52),
    ('NOI',  '営業外収益',         'PL', 53),
    ('NOE',  '営業外費用',         'PL', 54),
    ('EI',   '特別利益',           'PL', 55),
    ('EL',   '特別損失',           'PL', 56),
    ('TAX',  '法人税等',           'PL', 57);

-- ---- seed: 勘定科目（display_order はコード順） ----
-- 資産（流動）
INSERT OR IGNORE INTO accounts (code, name, account_type, category_id, dc_normal, default_tax_category_id, display_order)
SELECT v.code, v.name, v.atype, (SELECT id FROM account_categories WHERE code = v.cat), v.dc,
       (SELECT id FROM tax_categories WHERE code = v.tax), CAST(v.code AS INTEGER)
FROM (
    SELECT '1000' code, '現金'               name, 'asset' atype, 'CA' cat, 'D' dc, NULL tax UNION ALL
    SELECT '1010', '小口現金',               'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1020', '普通預金',               'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1030', '定期預金',               'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1100', '売掛金',                 'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1110', '未収入金',               'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1140', '前払費用',               'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1150', '立替金',                 'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1160', '仮払金',                 'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1200', '仕掛品',                 'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1900', '仮払消費税',             'asset', 'CA', 'D', NULL UNION ALL
    SELECT '1950', '貸倒引当金',             'asset', 'CA', 'C', NULL UNION ALL
    -- 資産（固定）
    SELECT '1500', '建物附属設備',           'asset', 'FAT', 'D', 'PUR_10' UNION ALL
    SELECT '1520', '工具器具備品',           'asset', 'FAT', 'D', 'PUR_10' UNION ALL
    SELECT '1560', 'ソフトウェア',           'asset', 'FAI', 'D', 'PUR_10' UNION ALL
    SELECT '1570', 'ソフトウェア仮勘定',     'asset', 'FAI', 'D', 'PUR_10' UNION ALL
    SELECT '1600', '敷金保証金',             'asset', 'FAO', 'D', NULL UNION ALL
    SELECT '1630', '長期前払費用',           'asset', 'FAO', 'D', NULL UNION ALL
    -- 負債（流動）
    SELECT '2000', '買掛金',                 'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2020', '未払金',                 'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2030', '未払費用',               'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2040', '短期借入金',             'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2050', '預り金',                 'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2060', '前受金',                 'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2110', '前受収益',               'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2200', '仮受消費税',             'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2210', '未払消費税等',           'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2220', '未払法人税等',           'liability', 'CL', 'C', NULL UNION ALL
    SELECT '2230', '賞与引当金',             'liability', 'CL', 'C', NULL UNION ALL
    -- 負債（固定）
    SELECT '2500', '長期借入金',             'liability', 'LL', 'C', NULL UNION ALL
    -- 純資産
    SELECT '3000', '資本金',                 'equity', 'EQC', 'C', NULL UNION ALL
    SELECT '3050', '資本準備金',             'equity', 'EQS', 'C', NULL UNION ALL
    SELECT '3100', '繰越利益剰余金',         'equity', 'EQR', 'C', NULL UNION ALL
    -- 売上高
    SELECT '4000', '受託開発売上高',         'revenue', 'REV', 'C', 'SALES_10' UNION ALL
    SELECT '4010', 'SES売上高',              'revenue', 'REV', 'C', 'SALES_10' UNION ALL
    SELECT '4020', 'SaaS売上高',             'revenue', 'REV', 'C', 'SALES_10' UNION ALL
    SELECT '4090', 'その他売上高',           'revenue', 'REV', 'C', 'SALES_10' UNION ALL
    -- 売上原価
    SELECT '5000', '外注費',                 'expense', 'COGS', 'D', 'PUR_10' UNION ALL
    SELECT '5100', '労務費',                 'expense', 'COGS', 'D', 'NON_TAXABLE' UNION ALL
    SELECT '5200', '原価経費',               'expense', 'COGS', 'D', 'PUR_10' UNION ALL
    SELECT '5900', '仕掛品振替高',           'expense', 'COGS', 'C', NULL UNION ALL
    -- 販管費
    SELECT '6000', '役員報酬',               'expense', 'SGA', 'D', 'NON_TAXABLE' UNION ALL
    SELECT '6010', '給料手当',               'expense', 'SGA', 'D', 'NON_TAXABLE' UNION ALL
    SELECT '6020', '賞与',                   'expense', 'SGA', 'D', 'NON_TAXABLE' UNION ALL
    SELECT '6030', '法定福利費',             'expense', 'SGA', 'D', 'PUR_EXEMPT' UNION ALL
    SELECT '6040', '福利厚生費',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6100', '旅費交通費',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6110', '交際費',                 'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6120', '会議費',                 'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6130', '通信費',                 'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6140', '消耗品費',               'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6150', '新聞図書費',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6160', '修繕費',                 'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6170', '広告宣伝費',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6180', '採用教育費',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6190', '支払報酬料',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6200', '地代家賃',               'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6210', '支払手数料',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6220', '水道光熱費',             'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6230', '保険料',                 'expense', 'SGA', 'D', 'PUR_EXEMPT' UNION ALL
    SELECT '6240', '租税公課',               'expense', 'SGA', 'D', 'NON_TAXABLE' UNION ALL
    SELECT '6250', 'リース料',               'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    SELECT '6300', '減価償却費',             'expense', 'SGA', 'D', 'OUT_OF_SCOPE' UNION ALL
    SELECT '6900', '雑費',                   'expense', 'SGA', 'D', 'PUR_10' UNION ALL
    -- 営業外
    SELECT '7000', '受取利息',               'revenue', 'NOI', 'C', 'SALES_EXEMPT' UNION ALL
    SELECT '7010', '雑収入',                 'revenue', 'NOI', 'C', 'SALES_10' UNION ALL
    SELECT '7500', '支払利息',               'expense', 'NOE', 'D', 'PUR_EXEMPT' UNION ALL
    SELECT '7510', '雑損失',                 'expense', 'NOE', 'D', 'OUT_OF_SCOPE' UNION ALL
    -- 特別損益
    SELECT '8000', '固定資産売却益',         'revenue', 'EI', 'C', 'SALES_10' UNION ALL
    SELECT '8500', '固定資産除却損',         'expense', 'EL', 'D', 'OUT_OF_SCOPE' UNION ALL
    -- 税金
    SELECT '9000', '法人税、住民税及び事業税','expense', 'TAX', 'D', 'OUT_OF_SCOPE'
) v;
