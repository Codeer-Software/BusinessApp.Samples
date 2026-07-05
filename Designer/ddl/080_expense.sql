-- 080_expense.sql — 経費精算の拡張（B2-2、AccountingSQLite）
-- 設計: docs/07_経費精算設計.md §2（基底: docs/references/経費精算.md）
-- 注意: FK 列に NOT NULL を付けない（Project.md 知見）。日付=DATE / 日時=DATETIME。

-- 費目マスタ（承認ルートを決める業務側の軸。勘定科目への橋渡しを持つ）
CREATE TABLE IF NOT EXISTS expense_categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    default_account_id INTEGER REFERENCES accounts(id),
    default_tax_category_id INTEGER REFERENCES tax_categories(id),
    is_entertainment INTEGER NOT NULL DEFAULT 0,   -- 1=交際費（例外項目必須・総務承認必須）
    is_asset_candidate INTEGER NOT NULL DEFAULT 0, -- 1=資産性の支出（備品等。10万以上で固定資産フラグ）
    display_order INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1
);

-- 経費申請の拡張列（既存 expense_request に追加）
ALTER TABLE expense_request ADD COLUMN request_type TEXT;          -- reimburse(立替精算) / advance(事前申請)
ALTER TABLE expense_request ADD COLUMN payee_type TEXT;            -- employee(社員へ精算) / partner(取引先へ支払)
ALTER TABLE expense_request ADD COLUMN expense_category_id INTEGER REFERENCES expense_categories(id);
ALTER TABLE expense_request ADD COLUMN used_date DATE;             -- 利用日（経費の発生日）
ALTER TABLE expense_request ADD COLUMN used_at TEXT;               -- 利用先（店名・レシート発行元）
ALTER TABLE expense_request ADD COLUMN tax_amount INTEGER;         -- 消費税額（レシート記載）
ALTER TABLE expense_request ADD COLUMN estimated_amount INTEGER;   -- 見込み額（事前申請のみ）
ALTER TABLE expense_request ADD COLUMN payee_user INTEGER REFERENCES app_users(id);      -- 精算対象者（社員へ精算）
ALTER TABLE expense_request ADD COLUMN payee_partner_id INTEGER REFERENCES partners(id); -- 支払取引先（取引先へ支払）
ALTER TABLE expense_request ADD COLUMN entertainment_guest TEXT;   -- 接待の相手先
ALTER TABLE expense_request ADD COLUMN entertainment_count INTEGER;-- 参加人数
ALTER TABLE expense_request ADD COLUMN entertainment_purpose TEXT; -- 接待の目的
ALTER TABLE expense_request ADD COLUMN is_fixed_asset INTEGER;     -- 固定資産計上対象フラグ
ALTER TABLE expense_request ADD COLUMN asset_no TEXT;              -- 資産管理番号
ALTER TABLE expense_request ADD COLUMN settlement_status TEXT;     -- draft/applying/approved/accounting/settled/completed
ALTER TABLE expense_request ADD COLUMN receipt_file_name TEXT;     -- 領収書（FileField 3列）
ALTER TABLE expense_request ADD COLUMN receipt_file_size INTEGER;
ALTER TABLE expense_request ADD COLUMN receipt_file_guid TEXT;

-- 部門に役職者（承認者の動的解決用）
ALTER TABLE departments ADD COLUMN manager_user INTEGER REFERENCES app_users(id);  -- 課長
ALTER TABLE departments ADD COLUMN director_user INTEGER REFERENCES app_users(id); -- 部長

-- 社員に所属部門
ALTER TABLE app_users ADD COLUMN department_id INTEGER REFERENCES departments(id);

-- ---- seed: 費目（references/経費精算.md §5。既定勘定科目・税区分への橋渡し付き） ----
INSERT OR IGNORE INTO expense_categories (code, name, default_account_id, default_tax_category_id, is_entertainment, is_asset_candidate, display_order)
SELECT v.code, v.name,
       (SELECT id FROM accounts WHERE code = v.acct),
       (SELECT id FROM tax_categories WHERE code = v.tax),
       v.ent, v.asset, v.ord
FROM (
    SELECT 'TRV' AS code, '旅費交通費' AS name, '6100' AS acct, 'PUR_10' AS tax, 0 AS ent, 0 AS asset, 10 AS ord UNION ALL
    SELECT 'MTG', '会議費',     '6120', 'PUR_10', 0, 0, 20 UNION ALL
    SELECT 'ENT', '交際費',     '6110', 'PUR_10', 1, 0, 30 UNION ALL
    SELECT 'SUP', '消耗品費',   '6140', 'PUR_10', 0, 0, 40 UNION ALL
    SELECT 'EQP', '備品',       '6140', 'PUR_10', 0, 1, 50 UNION ALL
    SELECT 'OUT', '外注費',     '5000', 'PUR_10', 0, 0, 60 UNION ALL
    SELECT 'COM', '通信費',     '6130', 'PUR_10', 0, 0, 70 UNION ALL
    SELECT 'BOK', '図書研修費', '6150', 'PUR_10', 0, 0, 80 UNION ALL
    SELECT 'DUE', '諸会費',     '6210', 'PUR_10', 0, 0, 90 UNION ALL
    SELECT 'ETC', 'その他',     '6900', 'PUR_10', 0, 0, 100
) v;
