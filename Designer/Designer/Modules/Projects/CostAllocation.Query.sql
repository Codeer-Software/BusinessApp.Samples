-- 人件費配賦: 対象年度×月の工数比率で monthly_salaries.cost を案件へ按分（切り捨て）
-- 配賦は管理会計レイヤ（仕訳なし。decisions/0009）。日付は date() 正規化。
-- U6-4（初見UXテスト）: 未配賦の可視化——工数未入力者のコスト行・切捨て端数行・検算合計行を追加し、
-- 「全行の配賦額合計 = 人件費コスト合計」が画面上で突合できるようにする。
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
),
alloc AS (
  SELECT
    p.code AS project_code,
    p.name AS project_name,
    u.name AS user_name,
    ROUND(te.mins / 60.0, 1) AS hours,
    ROUND(te.mins * 100.0 / tot.total_mins, 1) AS ratio_percent,
    COALESCE(ms.cost, 0) * te.mins / tot.total_mins AS allocated_cost,
    0 AS sort_key
  FROM te
  JOIN tot ON tot.user_id = te.user_id
  JOIN projects p ON p.id = te.project_id
  JOIN app_users u ON u.id = te.user_id
  LEFT JOIN monthly_salaries ms ON ms.user_id = te.user_id
    AND ms.fiscal_year_id = @fiscal_year_id AND ms.period_no = @period_no
),
unalloc AS (
  -- 工数未入力でコストが配賦されない人（月次締めの警戒対象）
  SELECT
    '⚠未配賦' AS project_code,
    '工数未入力（どの案件にも配賦されていません）' AS project_name,
    u.name AS user_name,
    0.0 AS hours,
    0.0 AS ratio_percent,
    ms.cost AS allocated_cost,
    1 AS sort_key
  FROM monthly_salaries ms
  JOIN app_users u ON u.id = ms.user_id
  WHERE ms.fiscal_year_id = @fiscal_year_id AND ms.period_no = @period_no
    AND ms.user_id NOT IN (SELECT user_id FROM tot)
),
frac AS (
  -- 円未満切捨てで消える端数（コスト合計との突合を成立させる調整行）
  SELECT
    '（端数）' AS project_code,
    '円未満切捨ての調整' AS project_name,
    '' AS user_name,
    0.0 AS hours,
    0.0 AS ratio_percent,
    (SELECT COALESCE(SUM(ms.cost), 0) FROM monthly_salaries ms
      WHERE ms.fiscal_year_id = @fiscal_year_id AND ms.period_no = @period_no
        AND ms.user_id IN (SELECT user_id FROM tot))
    - (SELECT COALESCE(SUM(allocated_cost), 0) FROM alloc) AS allocated_cost,
    2 AS sort_key
),
total AS (
  -- 検算: 配賦+未配賦+端数 = 人件費コスト合計（人件費コスト画面と一致するはず）
  SELECT
    '【合計】' AS project_code,
    '人件費コスト合計（配賦＋未配賦＋端数）' AS project_name,
    '' AS user_name,
    0.0 AS hours,
    0.0 AS ratio_percent,
    (SELECT COALESCE(SUM(ms.cost), 0) FROM monthly_salaries ms
      WHERE ms.fiscal_year_id = @fiscal_year_id AND ms.period_no = @period_no) AS allocated_cost,
    3 AS sort_key
)
SELECT project_code, project_name, user_name, hours, ratio_percent, allocated_cost
FROM (
  SELECT * FROM alloc
  UNION ALL
  SELECT * FROM unalloc
  UNION ALL
  SELECT * FROM frac WHERE allocated_cost <> 0
  UNION ALL
  SELECT * FROM total
)
ORDER BY sort_key, project_code, user_name
