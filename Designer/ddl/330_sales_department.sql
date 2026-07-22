-- 330_sales_department.sql — 販売伝票への部門付与（ユーザー承認 2026-07-23・AccountingSQLite）
-- 設計: 部門は案件ではなく伝票（見積・受注・請求書・定期請求契約）が直接持つ。
--   仕訳明細（journal_lines.department_id）が部門と案件を独立2軸で持つ構造と相似。
--   発行時点の所属を固定するスナップショット思想（ddl/270 経費申請と同じ）。
-- 任意入力（NULL=全社/共通）。既存データは作成者（creator）の現所属部門でバックフィル。
ALTER TABLE quotes ADD COLUMN department_id INTEGER REFERENCES departments(id);
ALTER TABLE sales_orders ADD COLUMN department_id INTEGER REFERENCES departments(id);
ALTER TABLE invoices ADD COLUMN department_id INTEGER REFERENCES departments(id);
ALTER TABLE recurring_billings ADD COLUMN department_id INTEGER REFERENCES departments(id);

UPDATE quotes
SET department_id = (SELECT u.department_id FROM app_users u WHERE u.id = quotes.creator)
WHERE department_id IS NULL;

UPDATE sales_orders
SET department_id = (SELECT u.department_id FROM app_users u WHERE u.id = sales_orders.creator)
WHERE department_id IS NULL;

UPDATE invoices
SET department_id = (SELECT u.department_id FROM app_users u WHERE u.id = invoices.creator)
WHERE department_id IS NULL;

UPDATE recurring_billings
SET department_id = (SELECT u.department_id FROM app_users u WHERE u.id = recurring_billings.creator)
WHERE department_id IS NULL;

-- creator を持たない seed 由来の契約は営業部（code 20）で補完
-- （SaaS 契約は営業獲得のため。デモデータの部門別分析が空にならないようにする）
UPDATE recurring_billings
SET department_id = (SELECT id FROM departments WHERE code = '20')
WHERE department_id IS NULL;
