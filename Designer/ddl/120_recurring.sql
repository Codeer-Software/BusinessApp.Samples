-- 120_recurring.sql — 定期請求（SaaS 月額、B-5、BusinessAppSQLite）
-- 設計: docs/08_請求入金設計.md / 売上計上は月次役務完了時=請求書一括生成と同時 (ADR-0008 の月次検収扱い)
-- 規律: FK 列に NOT NULL 禁止 / 日付=DATE (月は月初日で保存) / 金額=INTEGER 円

CREATE TABLE IF NOT EXISTS recurring_billings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    partner_id INTEGER REFERENCES partners(id),
    project_id INTEGER REFERENCES projects(id),
    title TEXT,                    -- 例: 「クラウド勤怠 SaaS 利用料」
    monthly_amount INTEGER,        -- 月額（税抜）
    start_month DATE,              -- 開始月（月初日で保存）
    end_month DATE,                -- NULL=継続中
    is_active INTEGER,
    note TEXT,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);

-- 冪等キー: recurring_billing_id × billing_month（同月の二重生成をスクリプトで防ぐ）
ALTER TABLE invoices ADD COLUMN recurring_billing_id INTEGER REFERENCES recurring_billings(id);
ALTER TABLE invoices ADD COLUMN billing_month DATE;

-- seed: アルタイル商事の SaaS 月額契約（SaaS 区分の案件があれば紐付け）
INSERT INTO recurring_billings (partner_id, project_id, title, monthly_amount, start_month, end_month, is_active)
SELECT
    (SELECT id FROM partners WHERE code = 'C001'),
    (SELECT id FROM projects WHERE project_type = 'saas' AND is_active = 1 ORDER BY id LIMIT 1),
    'クラウド勤怠 SaaS 利用料',
    100000,
    '2026-04-01',
    NULL,
    1
WHERE NOT EXISTS (SELECT 1 FROM recurring_billings WHERE title = 'クラウド勤怠 SaaS 利用料');
