-- 何を保証するか: 期首残高に損益科目（revenue / expense）が含まれていないこと。
-- 違反時の意味: PL 科目が翌期に繰り越されている。当期の売上・費用に前期分が混入し、
--               当期純利益が過大・過小になる。「PL 科目の翌期繰越は常にゼロ」という
--               決算の大原則が破れている。
-- 出典: docs/04_会計ドメイン設計.md §6 年次決算 2.・3.（BS 科目の期末残高のみコピー）
--       Modules/Accounting/FiscalYear.CarryOverSql.sql（account_type IN asset/liability/equity で絞る）
SELECT
    fy.name  AS 年度,
    a.code || ' ' || a.name AS 科目,
    a.account_type AS 科目区分,
    ob.balance     AS 期首残高
FROM opening_balances ob
JOIN fiscal_years fy ON fy.id = ob.fiscal_year_id
JOIN accounts a ON a.id = ob.account_id
WHERE a.account_type NOT IN ('asset', 'liability', 'equity')
ORDER BY fy.start_date, a.code
