-- 811_normalize_date_columns.sql — DATE 列の書式を「CLB が書く形」に揃える（BUG-0066）
--
-- 背景（2026-08-19 に実測して確定した事実）:
--   SQLite の日付比較は**文字列比較**。この DB の DATE 列には 2 種類の書式が混在していた。
--     * アプリ（CLB）が書いた行 …… 'YYYY-MM-DD 00:00:00'（19 文字）
--     * seed の DDL が書いた行   …… 'YYYY-MM-DD'（10 文字）
--   そして **CLB が `ModuleSearcher` の日付比較に渡すパラメータは 'YYYY-MM-DD 00:00:00' 側**である。
--   実測（FiscalPeriod.Status_OnDataChanged の下書き検知で確認）:
--     * 列 '2026-09-01'          >= パラメータ '2026-09-01 00:00:00' → **偽**（境界の行が落ちる）
--     * 列 '2026-09-01 00:00:00' >= パラメータ '2026-09-01 00:00:00' → 真
--   つまり「**時刻なしの行が、範囲の下端でだけ黙って消える**」。
--   同じ理屈で「期間解決」は上端で落ちる（列 end_date '2026-09-30' >= パラメータ '2026-09-30 00:00:00' が偽）。
--   コード中に散在する「境界日の罠を避けるため月初日で解決する」という回避策は、この現象への対症療法だった。
--
-- したがって**正しい正規形は「時刻なし」ではなく「00:00:00 付き」**である。
-- 時刻を落とす方向に揃えると、逆に**すべての下端比較が壊れる**（当初の台帳の見立ては誤り）。
--
-- 安全性の確認:
--   * `Modules/` 配下の `.sql` と不変条件 SQL は日付比較を必ず `date()` / `strftime()` で正規化しており、
--     この変更の影響を受けない（2026-08-19 に全文検査）
--   * 表示は CLB が DateTime としてパースするので変わらない
--   * この UPDATE は冪等（19 文字の行には触らない）
--
-- 再発防止: 不変条件 `F06_日付_DATE列の書式が揃っている` が 10 文字の値を検出する。
--           以後 seed DDL を足すときは日付リテラルに ' 00:00:00' を付けること。


UPDATE tax_rates SET valid_from = valid_from || ' 00:00:00' WHERE valid_from IS NOT NULL AND length(valid_from) = 10;
UPDATE tax_rates SET valid_to = valid_to || ' 00:00:00' WHERE valid_to IS NOT NULL AND length(valid_to) = 10;
UPDATE invoice_transition_rates SET valid_from = valid_from || ' 00:00:00' WHERE valid_from IS NOT NULL AND length(valid_from) = 10;
UPDATE invoice_transition_rates SET valid_to = valid_to || ' 00:00:00' WHERE valid_to IS NOT NULL AND length(valid_to) = 10;
UPDATE system_thresholds SET valid_from = valid_from || ' 00:00:00' WHERE valid_from IS NOT NULL AND length(valid_from) = 10;
UPDATE system_thresholds SET valid_to = valid_to || ' 00:00:00' WHERE valid_to IS NOT NULL AND length(valid_to) = 10;
UPDATE fiscal_years SET start_date = start_date || ' 00:00:00' WHERE start_date IS NOT NULL AND length(start_date) = 10;
UPDATE fiscal_years SET end_date = end_date || ' 00:00:00' WHERE end_date IS NOT NULL AND length(end_date) = 10;
UPDATE fiscal_periods SET start_date = start_date || ' 00:00:00' WHERE start_date IS NOT NULL AND length(start_date) = 10;
UPDATE fiscal_periods SET end_date = end_date || ' 00:00:00' WHERE end_date IS NOT NULL AND length(end_date) = 10;
UPDATE journal_entries SET entry_date = entry_date || ' 00:00:00' WHERE entry_date IS NOT NULL AND length(entry_date) = 10;
UPDATE fixed_assets SET acquisition_date = acquisition_date || ' 00:00:00' WHERE acquisition_date IS NOT NULL AND length(acquisition_date) = 10;
UPDATE fixed_assets SET retired_date = retired_date || ' 00:00:00' WHERE retired_date IS NOT NULL AND length(retired_date) = 10;
UPDATE expense_request SET expense_date = expense_date || ' 00:00:00' WHERE expense_date IS NOT NULL AND length(expense_date) = 10;
UPDATE expense_request SET used_date = used_date || ' 00:00:00' WHERE used_date IS NOT NULL AND length(used_date) = 10;
UPDATE quotes SET issue_date = issue_date || ' 00:00:00' WHERE issue_date IS NOT NULL AND length(issue_date) = 10;
UPDATE quotes SET valid_until = valid_until || ' 00:00:00' WHERE valid_until IS NOT NULL AND length(valid_until) = 10;
UPDATE sales_orders SET order_date = order_date || ' 00:00:00' WHERE order_date IS NOT NULL AND length(order_date) = 10;
UPDATE acceptances SET acceptance_date = acceptance_date || ' 00:00:00' WHERE acceptance_date IS NOT NULL AND length(acceptance_date) = 10;
UPDATE invoices SET issue_date = issue_date || ' 00:00:00' WHERE issue_date IS NOT NULL AND length(issue_date) = 10;
UPDATE invoices SET due_date = due_date || ' 00:00:00' WHERE due_date IS NOT NULL AND length(due_date) = 10;
UPDATE invoices SET billing_month = billing_month || ' 00:00:00' WHERE billing_month IS NOT NULL AND length(billing_month) = 10;
UPDATE receipts SET receipt_date = receipt_date || ' 00:00:00' WHERE receipt_date IS NOT NULL AND length(receipt_date) = 10;
UPDATE recurring_billings SET start_month = start_month || ' 00:00:00' WHERE start_month IS NOT NULL AND length(start_month) = 10;
UPDATE recurring_billings SET end_month = end_month || ' 00:00:00' WHERE end_month IS NOT NULL AND length(end_month) = 10;
UPDATE time_entries SET work_date = work_date || ' 00:00:00' WHERE work_date IS NOT NULL AND length(work_date) = 10;
UPDATE bank_statement_lines SET line_date = line_date || ' 00:00:00' WHERE line_date IS NOT NULL AND length(line_date) = 10;
UPDATE vendor_invoices SET received_date = received_date || ' 00:00:00' WHERE received_date IS NOT NULL AND length(received_date) = 10;
UPDATE vendor_invoices SET invoice_date = invoice_date || ' 00:00:00' WHERE invoice_date IS NOT NULL AND length(invoice_date) = 10;
UPDATE vendor_invoices SET due_date = due_date || ' 00:00:00' WHERE due_date IS NOT NULL AND length(due_date) = 10;
UPDATE vendor_invoices SET paid_date = paid_date || ' 00:00:00' WHERE paid_date IS NOT NULL AND length(paid_date) = 10;
UPDATE bank_statement_preview SET line_date = line_date || ' 00:00:00' WHERE line_date IS NOT NULL AND length(line_date) = 10;
UPDATE recurring_run_plan SET target_month = target_month || ' 00:00:00' WHERE target_month IS NOT NULL AND length(target_month) = 10;
UPDATE recurring_run_plan SET cycle_start = cycle_start || ' 00:00:00' WHERE cycle_start IS NOT NULL AND length(cycle_start) = 10;
UPDATE ses_run_plan SET target_month = target_month || ' 00:00:00' WHERE target_month IS NOT NULL AND length(target_month) = 10;
UPDATE cash_entry_drafts SET entry_date = entry_date || ' 00:00:00' WHERE entry_date IS NOT NULL AND length(entry_date) = 10;
UPDATE expense_request_lines SET used_date = used_date || ' 00:00:00' WHERE used_date IS NOT NULL AND length(used_date) = 10;
