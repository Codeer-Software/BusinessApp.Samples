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

-- ---- seed: 2026-06 の工数実績（部長 次郎 user_id=3・計 11,400 分 = 190h → 上限180h を 10h 超過） ----
INSERT OR IGNORE INTO time_entries (id, user_id, project_id, work_date, minutes, note) VALUES
    (101, 3, (SELECT id FROM projects WHERE code = 'PRJ-003'), '2026-06-08', 3000, 'SES 6月稼働 週1'),
    (102, 3, (SELECT id FROM projects WHERE code = 'PRJ-003'), '2026-06-15', 3000, 'SES 6月稼働 週2'),
    (103, 3, (SELECT id FROM projects WHERE code = 'PRJ-003'), '2026-06-22', 3000, 'SES 6月稼働 週3'),
    (104, 3, (SELECT id FROM projects WHERE code = 'PRJ-003'), '2026-06-29', 2400, 'SES 6月稼働 週4');
