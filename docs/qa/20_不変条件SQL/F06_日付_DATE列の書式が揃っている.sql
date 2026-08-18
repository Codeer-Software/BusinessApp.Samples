-- 何を保証するか: DATE 宣言のすべての列が 'YYYY-MM-DD 00:00:00'（19 文字）で保存されていること。
-- 違反時の意味: SQLite の日付比較は文字列比較で、**CLB が `ModuleSearcher` に渡すパラメータは
--               'YYYY-MM-DD 00:00:00' 側**である（2026-08-19 実測）。
--               時刻なし 'YYYY-MM-DD' の行が混ざると、
--                 列 '2026-09-01' >= パラメータ '2026-09-01 00:00:00' が **偽**になり、
--               **範囲検索の下端でその行だけが黙って消える**。同じ理屈で期間解決は上端で落ちる。
--               会計アプリでは「期末日の伝票が集計から抜ける」「締めチェックが下書きを見落とす」
--               という形で現れ、しかもエラーにならない。
-- 出典: docs/qa/02_バグ台帳.md BUG-0066 ／ Designer/ddl/811_normalize_date_columns.sql
-- 備考: DATETIME 列（created_at / updated_at）は対象外（時刻に意味がある）。
--       seed の DDL を追加するときは日付リテラルに ' 00:00:00' を付けること。

SELECT 'tax_rates.valid_from' AS 列, id AS 行id, valid_from AS 値 FROM tax_rates WHERE valid_from IS NOT NULL AND length(valid_from) <> 19
UNION ALL SELECT 'tax_rates.valid_to' AS 列, id AS 行id, valid_to AS 値 FROM tax_rates WHERE valid_to IS NOT NULL AND length(valid_to) <> 19
UNION ALL SELECT 'invoice_transition_rates.valid_from' AS 列, id AS 行id, valid_from AS 値 FROM invoice_transition_rates WHERE valid_from IS NOT NULL AND length(valid_from) <> 19
UNION ALL SELECT 'invoice_transition_rates.valid_to' AS 列, id AS 行id, valid_to AS 値 FROM invoice_transition_rates WHERE valid_to IS NOT NULL AND length(valid_to) <> 19
UNION ALL SELECT 'system_thresholds.valid_from' AS 列, id AS 行id, valid_from AS 値 FROM system_thresholds WHERE valid_from IS NOT NULL AND length(valid_from) <> 19
UNION ALL SELECT 'system_thresholds.valid_to' AS 列, id AS 行id, valid_to AS 値 FROM system_thresholds WHERE valid_to IS NOT NULL AND length(valid_to) <> 19
UNION ALL SELECT 'fiscal_years.start_date' AS 列, id AS 行id, start_date AS 値 FROM fiscal_years WHERE start_date IS NOT NULL AND length(start_date) <> 19
UNION ALL SELECT 'fiscal_years.end_date' AS 列, id AS 行id, end_date AS 値 FROM fiscal_years WHERE end_date IS NOT NULL AND length(end_date) <> 19
UNION ALL SELECT 'fiscal_periods.start_date' AS 列, id AS 行id, start_date AS 値 FROM fiscal_periods WHERE start_date IS NOT NULL AND length(start_date) <> 19
UNION ALL SELECT 'fiscal_periods.end_date' AS 列, id AS 行id, end_date AS 値 FROM fiscal_periods WHERE end_date IS NOT NULL AND length(end_date) <> 19
UNION ALL SELECT 'journal_entries.entry_date' AS 列, id AS 行id, entry_date AS 値 FROM journal_entries WHERE entry_date IS NOT NULL AND length(entry_date) <> 19
UNION ALL SELECT 'fixed_assets.acquisition_date' AS 列, id AS 行id, acquisition_date AS 値 FROM fixed_assets WHERE acquisition_date IS NOT NULL AND length(acquisition_date) <> 19
UNION ALL SELECT 'fixed_assets.retired_date' AS 列, id AS 行id, retired_date AS 値 FROM fixed_assets WHERE retired_date IS NOT NULL AND length(retired_date) <> 19
UNION ALL SELECT 'expense_request.expense_date' AS 列, id AS 行id, expense_date AS 値 FROM expense_request WHERE expense_date IS NOT NULL AND length(expense_date) <> 19
UNION ALL SELECT 'expense_request.used_date' AS 列, id AS 行id, used_date AS 値 FROM expense_request WHERE used_date IS NOT NULL AND length(used_date) <> 19
UNION ALL SELECT 'quotes.issue_date' AS 列, id AS 行id, issue_date AS 値 FROM quotes WHERE issue_date IS NOT NULL AND length(issue_date) <> 19
UNION ALL SELECT 'quotes.valid_until' AS 列, id AS 行id, valid_until AS 値 FROM quotes WHERE valid_until IS NOT NULL AND length(valid_until) <> 19
UNION ALL SELECT 'sales_orders.order_date' AS 列, id AS 行id, order_date AS 値 FROM sales_orders WHERE order_date IS NOT NULL AND length(order_date) <> 19
UNION ALL SELECT 'acceptances.acceptance_date' AS 列, id AS 行id, acceptance_date AS 値 FROM acceptances WHERE acceptance_date IS NOT NULL AND length(acceptance_date) <> 19
UNION ALL SELECT 'invoices.issue_date' AS 列, id AS 行id, issue_date AS 値 FROM invoices WHERE issue_date IS NOT NULL AND length(issue_date) <> 19
UNION ALL SELECT 'invoices.due_date' AS 列, id AS 行id, due_date AS 値 FROM invoices WHERE due_date IS NOT NULL AND length(due_date) <> 19
UNION ALL SELECT 'invoices.billing_month' AS 列, id AS 行id, billing_month AS 値 FROM invoices WHERE billing_month IS NOT NULL AND length(billing_month) <> 19
UNION ALL SELECT 'receipts.receipt_date' AS 列, id AS 行id, receipt_date AS 値 FROM receipts WHERE receipt_date IS NOT NULL AND length(receipt_date) <> 19
UNION ALL SELECT 'recurring_billings.start_month' AS 列, id AS 行id, start_month AS 値 FROM recurring_billings WHERE start_month IS NOT NULL AND length(start_month) <> 19
UNION ALL SELECT 'recurring_billings.end_month' AS 列, id AS 行id, end_month AS 値 FROM recurring_billings WHERE end_month IS NOT NULL AND length(end_month) <> 19
UNION ALL SELECT 'time_entries.work_date' AS 列, id AS 行id, work_date AS 値 FROM time_entries WHERE work_date IS NOT NULL AND length(work_date) <> 19
UNION ALL SELECT 'bank_statement_lines.line_date' AS 列, id AS 行id, line_date AS 値 FROM bank_statement_lines WHERE line_date IS NOT NULL AND length(line_date) <> 19
UNION ALL SELECT 'vendor_invoices.received_date' AS 列, id AS 行id, received_date AS 値 FROM vendor_invoices WHERE received_date IS NOT NULL AND length(received_date) <> 19
UNION ALL SELECT 'vendor_invoices.invoice_date' AS 列, id AS 行id, invoice_date AS 値 FROM vendor_invoices WHERE invoice_date IS NOT NULL AND length(invoice_date) <> 19
UNION ALL SELECT 'vendor_invoices.due_date' AS 列, id AS 行id, due_date AS 値 FROM vendor_invoices WHERE due_date IS NOT NULL AND length(due_date) <> 19
UNION ALL SELECT 'vendor_invoices.paid_date' AS 列, id AS 行id, paid_date AS 値 FROM vendor_invoices WHERE paid_date IS NOT NULL AND length(paid_date) <> 19
UNION ALL SELECT 'bank_statement_preview.line_date' AS 列, id AS 行id, line_date AS 値 FROM bank_statement_preview WHERE line_date IS NOT NULL AND length(line_date) <> 19
UNION ALL SELECT 'recurring_run_plan.target_month' AS 列, id AS 行id, target_month AS 値 FROM recurring_run_plan WHERE target_month IS NOT NULL AND length(target_month) <> 19
UNION ALL SELECT 'recurring_run_plan.cycle_start' AS 列, id AS 行id, cycle_start AS 値 FROM recurring_run_plan WHERE cycle_start IS NOT NULL AND length(cycle_start) <> 19
UNION ALL SELECT 'ses_run_plan.target_month' AS 列, id AS 行id, target_month AS 値 FROM ses_run_plan WHERE target_month IS NOT NULL AND length(target_month) <> 19
UNION ALL SELECT 'cash_entry_drafts.entry_date' AS 列, id AS 行id, entry_date AS 値 FROM cash_entry_drafts WHERE entry_date IS NOT NULL AND length(entry_date) <> 19
UNION ALL SELECT 'expense_request_lines.used_date' AS 列, id AS 行id, used_date AS 値 FROM expense_request_lines WHERE used_date IS NOT NULL AND length(used_date) <> 19
;
