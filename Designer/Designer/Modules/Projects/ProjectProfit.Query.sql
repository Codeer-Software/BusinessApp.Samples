-- 案件別損益: 売上（収益科目 C−D）− 直課費用（費用科目 D−C）− 配賦人件費（工数按分の年度合計）
-- 配賦ロジックは CostAllocation と同一（period ごとに按分し年度で SUM）。仕訳・工数が無い案件も 0 行で出す。
WITH rev AS (
  SELECT l.project_id, SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE -l.amount END) AS revenue
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  WHERE e.status = 'posted' AND e.fiscal_year_id = @fiscal_year_id
    AND a.account_type = 'revenue' AND l.project_id IS NOT NULL
  GROUP BY l.project_id
),
exp AS (
  SELECT l.project_id, SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS direct_cost
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  WHERE e.status = 'posted' AND e.fiscal_year_id = @fiscal_year_id
    AND a.account_type = 'expense' AND l.project_id IS NOT NULL
  GROUP BY l.project_id
),
te AS (
  SELECT fp.period_no, t.user_id, t.project_id, SUM(t.minutes) AS mins
  FROM time_entries t
  JOIN fiscal_periods fp ON fp.fiscal_year_id = @fiscal_year_id
    AND date(t.work_date) >= date(fp.start_date)
    AND date(t.work_date) <= date(fp.end_date)
  GROUP BY fp.period_no, t.user_id, t.project_id
),
tot AS (
  SELECT period_no, user_id, SUM(mins) AS total_mins FROM te GROUP BY period_no, user_id
),
alloc AS (
  SELECT te.project_id,
         SUM(COALESCE(ms.cost, 0) * te.mins / tot.total_mins) AS labor_cost
  FROM te
  JOIN tot ON tot.period_no = te.period_no AND tot.user_id = te.user_id
  LEFT JOIN monthly_salaries ms ON ms.user_id = te.user_id
    AND ms.fiscal_year_id = @fiscal_year_id AND ms.period_no = te.period_no
  GROUP BY te.project_id
)
SELECT
  p.code AS project_code,
  p.name AS project_name,
  CASE p.project_type WHEN 'contract' THEN '受託' WHEN 'ses' THEN 'SES' WHEN 'saas' THEN 'SaaS' ELSE p.project_type END AS project_type,
  COALESCE(rev.revenue, 0) AS revenue,
  COALESCE(exp.direct_cost, 0) AS direct_cost,
  COALESCE(alloc.labor_cost, 0) AS labor_cost,
  COALESCE(rev.revenue, 0) - COALESCE(exp.direct_cost, 0) - COALESCE(alloc.labor_cost, 0) AS gross_profit,
  CASE WHEN COALESCE(rev.revenue, 0) > 0
       THEN (COALESCE(rev.revenue, 0) - COALESCE(exp.direct_cost, 0) - COALESCE(alloc.labor_cost, 0)) * 100 / rev.revenue
       ELSE NULL END AS profit_rate
FROM projects p
LEFT JOIN rev ON rev.project_id = p.id
LEFT JOIN exp ON exp.project_id = p.id
LEFT JOIN alloc ON alloc.project_id = p.id
ORDER BY p.code
