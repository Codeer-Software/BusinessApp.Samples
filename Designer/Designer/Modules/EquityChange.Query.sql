-- 株主資本等変動計算書（簡易）: 純資産科目ごとの 期首残高 / 当期変動 / 当期純利益 / 期末残高
-- 当期純利益は繰越利益剰余金（コード 3100 固定・Project.md 知見）の行にのみ差込む（BalanceSheet と同じ計算）。
-- 純資産は貸方正で表示（opening_balances.balance は D 正の符号付きのため反転）。
WITH yr AS (
  SELECT id, start_date, end_date FROM fiscal_years WHERE id = @fiscal_year_id
),
eq AS (
  SELECT a.id, a.code, a.name FROM accounts a WHERE a.account_type = 'equity'
),
ob AS (
  SELECT account_id, SUM(balance) AS bal
  FROM opening_balances
  WHERE fiscal_year_id = @fiscal_year_id
  GROUP BY account_id
),
mv AS (
  SELECT l.account_id, SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE -l.amount END) AS chg
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND date(e.entry_date) >= (SELECT date(start_date) FROM yr)
    AND date(e.entry_date) <= (SELECT date(end_date) FROM yr)
  GROUP BY l.account_id
),
ni AS (
  SELECT COALESCE(SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE -l.amount END), 0) AS net_income
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN accounts a ON a.id = l.account_id
  WHERE e.status = 'posted'
    AND date(e.entry_date) >= (SELECT date(start_date) FROM yr)
    AND date(e.entry_date) <= (SELECT date(end_date) FROM yr)
    AND a.account_type IN ('revenue', 'expense')
),
final AS (
  SELECT
    eq.code AS account_code,
    eq.name AS account_name,
    COALESCE(-ob.bal, 0) AS opening_balance,
    COALESCE(mv.chg, 0) AS change_amount,
    CASE WHEN eq.code = '3100' THEN (SELECT net_income FROM ni) ELSE 0 END AS net_income,
    COALESCE(-ob.bal, 0) + COALESCE(mv.chg, 0)
      + CASE WHEN eq.code = '3100' THEN (SELECT net_income FROM ni) ELSE 0 END AS ending_balance
  FROM eq
  LEFT JOIN ob ON ob.account_id = eq.id
  LEFT JOIN mv ON mv.account_id = eq.id
  WHERE COALESCE(ob.bal, 0) <> 0 OR COALESCE(mv.chg, 0) <> 0
     OR eq.code IN ('3000', '3100')
)
SELECT account_code, account_name, opening_balance, change_amount, net_income, ending_balance
FROM final
UNION ALL
SELECT 'ZZZZ', '純資産合計',
  SUM(opening_balance), SUM(change_amount), SUM(net_income), SUM(ending_balance)
FROM final
ORDER BY account_code
