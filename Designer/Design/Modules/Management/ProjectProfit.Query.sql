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
    -- 仕掛品の期末振替・翌期首の振戻は**案件別損益に混ぜない**（ADR-0072）。
    -- 案件別損益は案件の生涯採算を見るもので、仕掛品は期間損益を正しくするための決算整理。
    -- 混ぜると「振り替えた瞬間に粗利が改善し、翌期に悪化する」ように見える。
    -- **仕掛品の計算に使う v_project_direct_cost とは除外の範囲が違う**（あちらは振戻を数える）ので、
    -- ビューを共用せずここに書く
    AND COALESCE(e.source_type, '') NOT IN ('wip', 'wip_reversal')
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
),
-- 人件費コストが未登録の月があるかを先頭行で言う（BUG-0430）。
-- alloc は `LEFT JOIN monthly_salaries` ＋ `COALESCE(ms.cost, 0)` なので、登録の無い月の工数は
-- **0 円で配賦される**。その分だけ配賦人件費が過少＝**粗利が過大**に出る。
-- 数字を勝手に補うことはできないので、「この数字を信用してよいか」を画面で言う。
-- 判定は `v_missing_salary`（工数はあるのに人件費が無い**人×月**）に寄せる——
-- 仕掛品・資金繰りと**同じ粒度**で判定するため。ここだけ「月に 1 行でもあれば登録済み」にすると
-- 部分登録の月を見逃す（BUG-0432 と同じ穴）
warn AS (
  SELECT
    0 AS sort_key,
    '⚠' AS project_code,
    '人件費コストが未登録の月があります（' || m.missing_count || ' 人月分）。'
      || 'その分の配賦人件費が 0 円で計算されているため、粗利が実態より大きく出ています。'
      || '経営管理 > 人件費コスト で登録してください' AS project_name,
    '' AS project_type,
    0 AS revenue, 0 AS direct_cost, 0 AS labor_cost, 0 AS gross_profit,
    NULL AS profit_rate
  FROM v_missing_salary m
  WHERE m.fiscal_year_id = @fiscal_year_id AND m.missing_count > 0
),
main AS (
  SELECT
    1 AS sort_key,
    p.code AS project_code,
    p.name AS project_name,
    -- internal（社内案件）が抜けていて生の英字が画面に出ていた
    CASE p.project_type
      WHEN 'contract' THEN '受託' WHEN 'ses' THEN 'SES' WHEN 'saas' THEN 'SaaS'
      WHEN 'internal' THEN '社内' ELSE COALESCE(p.project_type, '') END AS project_type,
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
)
SELECT project_code, project_name, project_type,
       revenue, direct_cost, labor_cost, gross_profit, profit_rate
FROM (SELECT * FROM warn UNION ALL SELECT * FROM main)
ORDER BY sort_key, project_code
