-- 翌期繰越（decisions/0006）。年度の Update Submit のたびに実行されるが、
-- next_year_id が NULL のときは何もしない no-op ガード付き。
-- 「翌期繰越を実行」ボタンが NextYearId をセットして Submit することで発火する。
-- ★パラメータ名はフィールド名ではなく DB 列名（@next_year_id）で解決される（ISSUE-0001 の真因・2026-07-08 実測）
DELETE FROM opening_balances
WHERE @next_year_id IS NOT NULL AND fiscal_year_id = @next_year_id;

INSERT INTO opening_balances (fiscal_year_id, account_id, sub_account_id, department_id, balance)
SELECT @next_year_id, t.account_id, NULL, NULL, t.bal
FROM (
  SELECT
    a.id AS account_id,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id = @id AND ob.account_id = a.id), 0)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              WHERE e.status = 'posted' AND l.account_id = a.id
                AND date(e.entry_date) >= (SELECT date(start_date) FROM fiscal_years WHERE id = @id)
                AND date(e.entry_date) <= (SELECT date(end_date) FROM fiscal_years WHERE id = @id)), 0)
    +
    CASE WHEN a.code = '3100' THEN
      COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
                FROM journal_lines l
                JOIN journal_entries e ON e.id = l.journal_entry_id
                JOIN accounts pa ON pa.id = l.account_id
                WHERE e.status = 'posted'
                  AND pa.account_type IN ('revenue', 'expense')
                  AND date(e.entry_date) >= (SELECT date(start_date) FROM fiscal_years WHERE id = @id)
                  AND date(e.entry_date) <= (SELECT date(end_date) FROM fiscal_years WHERE id = @id)), 0)
    ELSE 0 END AS bal
  FROM accounts a
  WHERE a.account_type IN ('asset', 'liability', 'equity')
) t
WHERE @next_year_id IS NOT NULL AND t.bal <> 0;
