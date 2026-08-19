-- 何を保証するか: 金額を入れる列に、整数でない値が 1 つも入っていないこと。
-- 違反時の意味: **CLB のスクリプトは整数どうしの計算でも小数に化ける**（FB-059）。
--               `var tax = 0;` のように `var` で受けた変数に `gross * pct / (100 + pct)` を代入すると、
--               454.5454… のような値がそのまま `journal_lines.amount` に入る。
--               `amount` は INTEGER 宣言だが、SQLite の型親和性では**非整数の REAL はそのまま格納される**。
--               借方・貸方とも同額なので **A01/A02/A05 も designcheck も不変条件も全部緑**のまま、
--               総勘定元帳・試算表・消費税集計表にだけ小数円が出る。
--               しかも金額が 11 の倍数（550 円などの典型的な振込手数料）のときは偶然割り切れるので、
--               **テストで踏みにくい**。
-- なぜ lint では足りないか: `lint_design.py` の CLB-040 は**ソースの形**を見るだけで、
--               すでに DB に入ってしまった値は誰も見ていない。ここが唯一の検出手段になる。
-- 出典: docs/qa/02_バグ台帳.md BUG-0421 / BUG-0437 ／ docs/12_CLB改善提案 FB-059

SELECT 'journal_lines.amount' AS 列, id AS 行id, CAST(amount AS TEXT) AS 値, typeof(amount) AS 型
FROM journal_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'journal_lines.input_amount', id, CAST(input_amount AS TEXT), typeof(input_amount)
FROM journal_lines WHERE input_amount IS NOT NULL AND input_amount <> CAST(input_amount AS INTEGER)
UNION ALL
SELECT 'opening_balances.balance', id, CAST(balance AS TEXT), typeof(balance)
FROM opening_balances WHERE balance IS NOT NULL AND balance <> CAST(balance AS INTEGER)
UNION ALL
SELECT 'invoices.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM invoices WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'invoices.tax_amount', id, CAST(tax_amount AS TEXT), typeof(tax_amount)
FROM invoices WHERE tax_amount IS NOT NULL AND tax_amount <> CAST(tax_amount AS INTEGER)
UNION ALL
SELECT 'invoice_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM invoice_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'acceptances.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM acceptances WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'acceptances.tax_amount', id, CAST(tax_amount AS TEXT), typeof(tax_amount)
FROM acceptances WHERE tax_amount IS NOT NULL AND tax_amount <> CAST(tax_amount AS INTEGER)
UNION ALL
SELECT 'acceptance_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM acceptance_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'quote_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM quote_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'sales_order_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM sales_order_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'receipts.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM receipts WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'receipt_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM receipt_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'receipt_lines.diff_amount', id, CAST(diff_amount AS TEXT), typeof(diff_amount)
FROM receipt_lines WHERE diff_amount IS NOT NULL AND diff_amount <> CAST(diff_amount AS INTEGER)
UNION ALL
SELECT 'expense_request.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM expense_request WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'expense_request.tax_amount', id, CAST(tax_amount AS TEXT), typeof(tax_amount)
FROM expense_request WHERE tax_amount IS NOT NULL AND tax_amount <> CAST(tax_amount AS INTEGER)
UNION ALL
SELECT 'expense_request_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM expense_request_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'expense_request_lines.tax_amount', id, CAST(tax_amount AS TEXT), typeof(tax_amount)
FROM expense_request_lines WHERE tax_amount IS NOT NULL AND tax_amount <> CAST(tax_amount AS INTEGER)
UNION ALL
SELECT 'vendor_invoices.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM vendor_invoices WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'fixed_assets.acquisition_cost', id, CAST(acquisition_cost AS TEXT), typeof(acquisition_cost)
FROM fixed_assets WHERE acquisition_cost IS NOT NULL AND acquisition_cost <> CAST(acquisition_cost AS INTEGER)
UNION ALL
SELECT 'bank_statement_lines.amount_in', id, CAST(amount_in AS TEXT), typeof(amount_in)
FROM bank_statement_lines WHERE amount_in IS NOT NULL AND amount_in <> CAST(amount_in AS INTEGER)
UNION ALL
SELECT 'bank_statement_lines.amount_out', id, CAST(amount_out AS TEXT), typeof(amount_out)
FROM bank_statement_lines WHERE amount_out IS NOT NULL AND amount_out <> CAST(amount_out AS INTEGER)
UNION ALL
SELECT 'budget_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM budget_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
UNION ALL
SELECT 'monthly_salaries.cost', id, CAST(cost AS TEXT), typeof(cost)
FROM monthly_salaries WHERE cost IS NOT NULL AND cost <> CAST(cost AS INTEGER)
UNION ALL
SELECT 'journal_template_lines.amount', id, CAST(amount AS TEXT), typeof(amount)
FROM journal_template_lines WHERE amount IS NOT NULL AND amount <> CAST(amount AS INTEGER)
