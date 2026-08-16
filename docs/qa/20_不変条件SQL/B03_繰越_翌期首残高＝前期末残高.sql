-- 何を保証するか: 翌期の期首残高が、前期の期末残高（前期首 + 前期の確定仕訳）と一致すること。
--                 対象は BS 科目のうち繰越利益剰余金(3100) 以外（3100 は B04 で当期純利益込みで検証）。
-- 違反時の意味: 繰越がズレている。翌期の BS が前期の BS と繋がらず、
--               「どこで消えた／増えたか」を追えない最悪の不整合。
--               翌期繰越の実行後に前期の伝票を追加・修正すると必ずこうなる（再繰越が必要）。
-- 出典: docs/04_会計ドメイン設計.md §6 年次決算 3.／decisions/0006（損益振替仕訳を作らない繰越方式）
--       Modules/Accounting/FiscalYear.CarryOverSql.sql（本チェックはこの SQL の逆算）
-- 備考: 年度の連結は fiscal_years.next_year_id ではなく日付の連続（前期末 + 1 日 = 翌期首）で行う。
--       next_year_id は繰越実行のトリガ用で、実データでは NULL のまま残ることがあるため。
--       期首残高が 1 行も無い年度（未繰越）は前後どちらも対象外。導入初年度の期首残高は
--       繰越ではなく手入力で投入する（§6）ため、その年度への「繰越の一致」は成立しない。
WITH yr AS (
  SELECT id, name, date(start_date) AS sd, date(end_date) AS ed FROM fiscal_years
),
pair AS (
  SELECT p.id AS prev_id, p.name AS prev_name, p.sd AS prev_sd, p.ed AS prev_ed,
         n.id AS next_id, n.name AS next_name
  FROM yr p
  JOIN yr n ON n.sd = date(p.ed, '+1 day')
  WHERE EXISTS (SELECT 1 FROM opening_balances ob WHERE ob.fiscal_year_id = n.id)
    AND EXISTS (SELECT 1 FROM opening_balances ob WHERE ob.fiscal_year_id = p.id)
),
calc AS (
  SELECT
    pr.prev_sd   AS 並び順,
    pr.prev_name AS 前期,
    pr.next_name AS 翌期,
    a.code || ' ' || a.name AS 科目,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id = pr.prev_id AND ob.account_id = a.id), 0) AS 前期首,
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l JOIN journal_entries e ON e.id = l.journal_entry_id
              WHERE e.status = 'posted' AND l.account_id = a.id
                AND date(e.entry_date) >= pr.prev_sd AND date(e.entry_date) <= pr.prev_ed), 0) AS 前期増減,
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              WHERE ob.fiscal_year_id = pr.next_id AND ob.account_id = a.id), 0) AS 翌期首
  FROM pair pr
  CROSS JOIN accounts a
  WHERE a.account_type IN ('asset', 'liability', 'equity')
    AND a.code <> '3100'
)
SELECT 前期, 翌期, 科目, 前期首, 前期増減,
       前期首 + 前期増減 AS 前期末, 翌期首,
       翌期首 - (前期首 + 前期増減) AS 差額
FROM calc
WHERE 翌期首 <> 前期首 + 前期増減
ORDER BY 並び順, 科目
