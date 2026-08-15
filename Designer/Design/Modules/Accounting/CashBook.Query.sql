-- 出納帳: 対象の現預金科目 (@account_code) の入出金明細と累計残高
-- 正典: GeneralLedger.Query.sql（期首残高 + window 関数の累計、諸口判定）
-- 現預金は借方増: 入金=D 行 / 出金=C 行
WITH acct AS (
  SELECT id, dc_normal FROM accounts WHERE code = @account_code
),
yr AS (
  SELECT id, start_date FROM fiscal_years
  WHERE date(start_date) <= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
    AND date(end_date) >= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
),
base AS (
  SELECT
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id IN (SELECT id FROM yr)
                AND ob.account_id IN (SELECT id FROM acct)), 0)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              WHERE e.status = 'posted'
                AND l.account_id IN (SELECT id FROM acct)
                AND date(e.entry_date) >= (SELECT date(start_date) FROM yr)
                AND @date_from IS NOT NULL
                AND date(e.entry_date) < date(@date_from)), 0) AS dmc
)
SELECT
  e.entry_date,
  e.journal_no,
  l.line_no,
  CASE
    WHEN (SELECT COUNT(*) FROM journal_lines x WHERE x.journal_entry_id = e.id AND x.id <> l.id) = 1
      THEN (SELECT a2.name FROM journal_lines x JOIN accounts a2 ON a2.id = x.account_id
            WHERE x.journal_entry_id = e.id AND x.id <> l.id)
    ELSE '諸口'
  END AS counter_account_name,
  COALESCE(l.description, e.description, '') AS line_description,
  CASE WHEN l.dc = 'D' THEN l.amount END AS deposit_amount,
  CASE WHEN l.dc = 'C' THEN l.amount END AS withdrawal_amount,
  (SELECT dmc FROM base) * (CASE WHEN a.dc_normal = 'D' THEN 1 ELSE -1 END)
  + SUM((CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
        * (CASE WHEN a.dc_normal = 'D' THEN 1 ELSE -1 END))
      OVER (ORDER BY date(e.entry_date), e.journal_no, l.line_no
            ROWS UNBOUNDED PRECEDING) AS balance
FROM journal_lines l
JOIN journal_entries e ON e.id = l.journal_entry_id
JOIN accounts a ON a.id = l.account_id
WHERE e.status = 'posted'
  AND l.account_id IN (SELECT id FROM acct)
  AND date(e.entry_date) >= COALESCE(date(@date_from), (SELECT date(start_date) FROM yr))
  AND (@date_to IS NULL OR date(e.entry_date) <= date(@date_to))
ORDER BY date(e.entry_date), e.journal_no, l.line_no
