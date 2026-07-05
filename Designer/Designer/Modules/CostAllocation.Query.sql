-- 人件費配賦: 対象年度×月の工数比率で monthly_salaries.cost を案件へ按分（切り捨て）
-- 配賦は管理会計レイヤ（仕訳なし。decisions/0009）。日付は date() 正規化。
WITH pr AS (
  SELECT start_date, end_date FROM fiscal_periods
  WHERE fiscal_year_id = @fiscal_year_id AND period_no = @period_no
),
te AS (
  SELECT t.user_id, t.project_id, SUM(t.minutes) AS mins
  FROM time_entries t
  WHERE date(t.work_date) >= (SELECT date(start_date) FROM pr)
    AND date(t.work_date) <= (SELECT date(end_date) FROM pr)
  GROUP BY t.user_id, t.project_id
),
tot AS (
  SELECT user_id, SUM(mins) AS total_mins FROM te GROUP BY user_id
)
SELECT
  p.code AS project_code,
  p.name AS project_name,
  u.name AS user_name,
  ROUND(te.mins / 60.0, 1) AS hours,
  te.mins * 100 / tot.total_mins AS ratio_percent,
  COALESCE(ms.cost, 0) * te.mins / tot.total_mins AS allocated_cost
FROM te
JOIN tot ON tot.user_id = te.user_id
JOIN projects p ON p.id = te.project_id
JOIN app_users u ON u.id = te.user_id
LEFT JOIN monthly_salaries ms ON ms.user_id = te.user_id
  AND ms.fiscal_year_id = @fiscal_year_id AND ms.period_no = @period_no
ORDER BY p.code, u.user_name
