-- 何を保証するか: 会計年度ごと（期首残高 + その年度の確定仕訳）に貸借対照表等式が成立すること。
--                   資産 = 負債 + 純資産 + 当期純利益
-- 違反時の意味: BS が貸借一致しない。原因は必ず「仕訳が不一致（A01/A02）」か
--               「期首残高が不一致（B01）」のどちらかなので、この 3 本をセットで見ると切り分けできる。
-- 出典: docs/04_会計ドメイン設計.md §5（BS: 当期純利益は PL から算出して純資産の部に差し込む）
--       Modules/FinancialReports/BalanceSheet.Query.sql / EquityChange.Query.sql と同じ組み立て
-- 符号: 内部計算は符号付き dmc（借方 - 貸方）で行う。表示列は各区分の正残側に直している。
WITH yr AS (
  SELECT id, name, date(start_date) AS sd, date(end_date) AS ed FROM fiscal_years
),
dmc AS (
  SELECT
    y.id AS fy_id, y.name AS fy_name, y.sd AS 並び順,
    a.account_type AS t,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id = y.id AND ob.account_id = a.id), 0)
    + COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
                FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
                WHERE e.status = 'posted' AND l.account_id = a.id
                  AND date(e.entry_date) >= y.sd AND date(e.entry_date) <= y.ed), 0) AS v
  FROM yr y
  CROSS JOIN accounts a
),
agg AS (
  SELECT fy_id, fy_name, 並び順,
         SUM(CASE WHEN t = 'asset'     THEN v ELSE 0 END)  AS 資産,
         SUM(CASE WHEN t = 'liability' THEN -v ELSE 0 END) AS 負債,
         SUM(CASE WHEN t = 'equity'    THEN -v ELSE 0 END) AS 純資産,
         SUM(CASE WHEN t IN ('revenue', 'expense') THEN -v ELSE 0 END) AS 当期純利益
  FROM dmc
  GROUP BY fy_id
)
SELECT fy_name AS 年度, 資産, 負債, 純資産, 当期純利益,
       資産 - (負債 + 純資産 + 当期純利益) AS 差額
FROM agg
WHERE 資産 <> 負債 + 純資産 + 当期純利益
ORDER BY 並び順
