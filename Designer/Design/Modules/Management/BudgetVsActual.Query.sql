-- 予実対比: 部門×費用科目ごとの 年間予算 / 実績 / 差異 / 消化率 / 警告
-- 警告率は system_thresholds.BUDGET_ALERT_RATE を参照（ハードコード禁止）
-- 部門未設定の実績（journal_lines.department_id IS NULL）も「(部門未設定)」行で可視化する
WITH b AS (
  SELECT department_id, account_id, SUM(amount) AS budget
  FROM budget_lines
  WHERE fiscal_year_id = @fiscal_year_id
  GROUP BY department_id, account_id
),
act AS (
  SELECT l.department_id, l.account_id,
         SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS actual
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  WHERE e.status = 'posted'
    AND e.fiscal_year_id = @fiscal_year_id
    AND a.account_type = 'expense'
  GROUP BY l.department_id, l.account_id
),
keys AS (
  SELECT department_id, account_id FROM b
  UNION
  SELECT department_id, account_id FROM act
),
alert_rate AS (
  SELECT amount AS rate FROM system_thresholds WHERE code = 'BUDGET_ALERT_RATE' LIMIT 1
)
SELECT
  COALESCE(d.name, '(部門未設定)') AS department_name,
  a.code AS account_code,
  a.name AS account_name,
  COALESCE(b.budget, 0) AS budget_amount,
  COALESCE(act.actual, 0) AS actual_amount,
  COALESCE(b.budget, 0) - COALESCE(act.actual, 0) AS diff_amount,
  CASE WHEN COALESCE(b.budget, 0) > 0
       THEN COALESCE(act.actual, 0) * 100 / b.budget
       ELSE NULL END AS usage_rate,
  CASE WHEN COALESCE(b.budget, 0) > 0
        AND COALESCE(act.actual, 0) * 100 / b.budget >= (SELECT rate FROM alert_rate)
       THEN '⚠ 予算警告' ELSE '' END AS alert_mark
FROM keys k
JOIN accounts a ON a.id = k.account_id
LEFT JOIN departments d ON d.id = k.department_id
LEFT JOIN b ON b.department_id IS k.department_id AND b.account_id = k.account_id
LEFT JOIN act ON act.department_id IS k.department_id AND act.account_id = k.account_id
WHERE (@department_id IS NULL OR k.department_id = @department_id)
ORDER BY COALESCE(d.display_order, 9999), a.code
