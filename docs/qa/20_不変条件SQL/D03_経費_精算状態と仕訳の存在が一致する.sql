-- 何を保証するか: 経費申請の settlement_status と、計上仕訳・支払仕訳の存在が一致すること。
--   状態遷移: draft → applying → approved → accounting（計上仕訳 source_type='expense'）
--             → settled（支払仕訳 source_type='expense_payment'）→ completed
--   ・accounting / settled / completed → 計上仕訳がある
--   ・settled / completed             → 支払仕訳がある
--   ・draft / applying / approved     → まだどちらの仕訳も無い
-- 違反時の意味: 未払金（従業員立替）の残高が実態と合わない。二重精算・精算漏れ。
-- 出典: Modules/Expense/ExpenseRequest.mod.cs（SettlementStatus の遷移と起票箇所）
-- 注意: この不変条件は「経費申請の明細行化」改修で最も壊れやすい。改修前後で必ず比較すること。
SELECT '計上仕訳が無い' AS 違反, er.id AS 申請id, er.title AS 件名,
       er.settlement_status AS 精算状態, er.amount AS 金額, er.expense_date AS 申請日
FROM expense_request er
WHERE er.settlement_status IN ('accounting', 'settled', 'completed')
  AND NOT EXISTS (SELECT 1 FROM journal_entries je
                  WHERE je.source_type = 'expense' AND je.source_id = er.id)

UNION ALL
SELECT '支払仕訳が無い', er.id, er.title, er.settlement_status, er.amount, er.expense_date
FROM expense_request er
WHERE er.settlement_status IN ('settled', 'completed')
  AND NOT EXISTS (SELECT 1 FROM journal_entries je
                  WHERE je.source_type = 'expense_payment' AND je.source_id = er.id)

UNION ALL
SELECT '未計上なのに計上仕訳がある', er.id, er.title, er.settlement_status, er.amount, er.expense_date
FROM expense_request er
WHERE er.settlement_status IN ('draft', 'applying', 'approved')
  AND EXISTS (SELECT 1 FROM journal_entries je
              WHERE je.source_type IN ('expense', 'expense_payment') AND je.source_id = er.id)

UNION ALL
SELECT '計上仕訳が複数ある', er.id, er.title, er.settlement_status, er.amount, er.expense_date
FROM expense_request er
WHERE (SELECT COUNT(*) FROM journal_entries je
       WHERE je.source_type = 'expense' AND je.source_id = er.id) > 1

UNION ALL
SELECT '支払仕訳が複数ある', er.id, er.title, er.settlement_status, er.amount, er.expense_date
FROM expense_request er
WHERE (SELECT COUNT(*) FROM journal_entries je
       WHERE je.source_type = 'expense_payment' AND je.source_id = er.id) > 1
