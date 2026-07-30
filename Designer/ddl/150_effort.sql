-- 150_effort.sql — 工数・人件費配賦・案件損益（B'、BusinessAppSQLite）
-- 設計: docs/decisions/0009（配賦は管理会計レイヤ=仕訳なし・工数は分単位 INTEGER）
-- 規律: FK 列に NOT NULL 禁止 / 日付=DATE

CREATE TABLE IF NOT EXISTS time_entries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER REFERENCES app_users(id),
    project_id INTEGER REFERENCES projects(id),
    work_date DATE,
    minutes INTEGER,               -- 分単位（decisions/0009）
    note TEXT,
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);
CREATE INDEX IF NOT EXISTS idx_time_entries_up ON time_entries(user_id, project_id, work_date);

CREATE TABLE IF NOT EXISTS monthly_salaries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER REFERENCES app_users(id),
    fiscal_year_id INTEGER REFERENCES fiscal_years(id),
    period_no INTEGER,             -- 1〜12
    cost INTEGER,                  -- 配賦用の月次人件費コスト（法定福利込み概算）
    creator INTEGER REFERENCES app_users(id),
    updater INTEGER REFERENCES app_users(id),
    created_at DATETIME,
    updated_at DATETIME
);
CREATE INDEX IF NOT EXISTS idx_monthly_salaries_key ON monthly_salaries(fiscal_year_id, period_no, user_id);

-- ---- seed: 案件（active 案件が無い場合のみ） ----
INSERT INTO projects (code, name, partner_id, project_type, status, is_active)
SELECT 'PRJ-001', '基幹システム改修', (SELECT id FROM partners WHERE code = 'C001'), 'contract', 'active', 1
WHERE NOT EXISTS (SELECT 1 FROM projects WHERE code = 'PRJ-001');
INSERT INTO projects (code, name, partner_id, project_type, status, is_active)
SELECT 'PRJ-002', 'クラウド勤怠SaaS', (SELECT id FROM partners WHERE code = 'C001'), 'saas', 'active', 1
WHERE NOT EXISTS (SELECT 1 FROM projects WHERE code = 'PRJ-002');

-- 120_recurring.sql の月額契約 seed は projects より先に走るため project_id が NULL 解決になる
-- （2026-07-10 の実機実行で発覚: 月額売上仕訳に案件が付かず案件損益から漏れる）。ここで補完する。
UPDATE recurring_billings
SET project_id = (SELECT id FROM projects WHERE code = 'PRJ-002')
WHERE project_id IS NULL AND title = 'クラウド勤怠 SaaS 利用料';

-- 月次人件費コストの seed は 2026-07-09 の組織再編（docs/decisions/0018）で廃止した。
-- 旧ユーザー（admin/hanako/jiro/soumu）前提だったため。テストデータは E2E シナリオ
-- （docs/tests/11）が画面から登録する（まっさら DB では 0 件が正しい状態）。
