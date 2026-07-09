-- 220_ses.sql — SES 精算幅（B'-5）
-- projects に SES 契約条件（月額基本料金・精算幅・控除/超過単価）を追加し、
-- SES 案件と 2026-06 の工数実績を seed する。
-- 注意: ALTER TABLE ADD COLUMN は再実行時に duplicate column エラーになる（害はないが、
--       再実行する場合は ALTER 5 行を除いて流すこと）。INSERT は OR IGNORE で冪等。

ALTER TABLE projects ADD COLUMN ses_monthly_rate INTEGER;  -- 月額基本料金（税抜）
ALTER TABLE projects ADD COLUMN ses_lower_hours INTEGER;   -- 精算下限（h）
ALTER TABLE projects ADD COLUMN ses_upper_hours INTEGER;   -- 精算上限（h）
ALTER TABLE projects ADD COLUMN ses_deduct_rate INTEGER;   -- 控除単価（円/h・下限未達）
ALTER TABLE projects ADD COLUMN ses_excess_rate INTEGER;   -- 超過単価（円/h・上限超過）

-- ---- seed: SES 案件（金融系SES・精算幅 140-180h） ----
INSERT OR IGNORE INTO projects (id, code, name, partner_id, project_type, status, is_active,
    ses_monthly_rate, ses_lower_hours, ses_upper_hours, ses_deduct_rate, ses_excess_rate) VALUES
    (3, 'PRJ-003', '金融系SES 山田',
     (SELECT id FROM partners WHERE code = 'C001'),
     'ses', 'active', 1,
     800000, 140, 180, 5000, 4500);

-- 2026-06 の工数実績 seed は 2026-07-09 の組織再編（docs/decisions/0018）で廃止した。
-- user_id=3 の直値が旧ユーザー（次郎）前提で、新組織では別人に紐づいてしまうため。
-- 工数のテストデータは E2E シナリオ（docs/11）が画面から登録する。
