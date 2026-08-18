-- ポータル「アラート」件数（ADR-0045・docs/13 §3 #7-#10 の契約。1 行）
-- 支払期限 = 支払予定表（PaymentSchedule）と同一条件。「まもなく」日数は system_thresholds.PAY_DUE_SOON_DAYS
-- 期限超過の売掛 = 売掛残高一覧（ReceivableBalance）の state='期限超過' と同一条件
-- 資金ショート = 資金繰り予測（CashFlowForecastData）の alert_mark と同一モデル（当月+3ヶ月・期末資金<0 の月数）
-- 予算警告 = 予実対比（BudgetVsActual）の alert_mark と同一条件の部門数（当年度・BUDGET_ALERT_RATE）
-- 入金の集計は 3 帳票とも「消込済み（消込仕訳がある）入金」だけを数える。発行時に自動作成される
-- 未確定の入金予定（ADR-0032）を含めると期限超過が 0 件・入金予定が 0 円になる（改善候補 A-2）
WITH RECURSIVE months(idx, month_first) AS (
  SELECT 0, date('now', 'localtime', 'start of month')
  UNION ALL
  SELECT idx + 1, date(month_first, '+1 month') FROM months WHERE idx < 3
),
threshold AS (
  SELECT COALESCE((SELECT amount FROM system_thresholds WHERE code = 'PAY_DUE_SOON_DAYS' LIMIT 1), 7) AS days
),
pay AS (
  SELECT CAST(julianday(date(v.due_date)) - julianday(date('now', 'localtime')) AS INTEGER) AS days_left
  FROM vendor_invoices v
  WHERE v.status IN ('received', 'accrued')
),
recv AS (
  SELECT count(*) AS c
  FROM invoices i
  LEFT JOIN (SELECT r.invoice_id AS invoice_id, SUM(r.amount) AS received
             FROM receipts r
             WHERE EXISTS (SELECT 1 FROM journal_entries je
                           WHERE je.source_type = 'receipt' AND je.source_id = r.id)
             GROUP BY r.invoice_id) rc
    ON rc.invoice_id = i.id
  WHERE i.status <> 'void' AND i.status <> 'draft' AND i.status <> 'paid'
    AND COALESCE(rc.received, 0) < COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0)
    AND i.due_date IS NOT NULL AND date(i.due_date) < date('now', 'localtime')
),
cur_yr AS (
  SELECT id FROM fiscal_years
  WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')
),
cash_now AS (
  SELECT
    COALESCE((SELECT SUM(ob.balance)
              FROM opening_balances ob JOIN accounts a ON a.id = ob.account_id
              WHERE a.code IN ('1000', '1010', '1020')
                AND ob.fiscal_year_id IN (SELECT id FROM cur_yr)), 0)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
              WHERE e.status = 'posted' AND a.code IN ('1000', '1010', '1020')
                AND e.fiscal_year_id IN (SELECT id FROM cur_yr)), 0) AS cash
),
-- 売上の既定税区分（税制マスタで設定: tax_categories.default_for='sales'）に紐づく税率
sales_rate AS (
  -- 既定用途='売上' の税区分が無い／無効のとき、0% にフォールバックしてはいけない。
  -- 入金見込みが税抜のまま（約 10% 過小）になり、警告も出ないので気づけない。
  -- 税率は直書きせず（CLAUDE.md §3）、**有効な課税売上区分の最高税率**を代わりに使う
  SELECT COALESCE(
           (SELECT tr.rate_percent
            FROM tax_categories tc JOIN tax_rates tr ON tr.id = tc.tax_rate_id
            WHERE tc.default_for = 'sales' AND tc.is_active = 1),
           (SELECT MAX(tr2.rate_percent)
            FROM tax_categories tc2 JOIN tax_rates tr2 ON tr2.id = tc2.tax_rate_id
            WHERE tc2.taxation_type = 'taxable_sales' AND tc2.is_active = 1),
           0) AS pct
),
inv_in AS (
  SELECT max(date(i.due_date, 'start of month'), (SELECT month_first FROM months WHERE idx = 0)) AS m,
         COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) - COALESCE(rc.received, 0) AS amt
  FROM invoices i
  LEFT JOIN (SELECT r.invoice_id AS invoice_id, SUM(r.amount) AS received
             FROM receipts r
             WHERE EXISTS (SELECT 1 FROM journal_entries je
                           WHERE je.source_type = 'receipt' AND je.source_id = r.id)
             GROUP BY r.invoice_id) rc
    ON rc.invoice_id = i.id
  WHERE i.status IN ('issued', 'partial') AND i.due_date IS NOT NULL
),
rec_in AS (
  SELECT date(mm.month_first, '+1 month') AS m,
         rb.monthly_amount * (100 + (SELECT pct FROM sales_rate)) / 100 AS amt
  FROM months mm
  -- 確定済のみ（ADR-0057）。下書き・終了は「定期請求の実行」の対象外なので入金見込みにも載せない
  JOIN recurring_billings rb ON rb.is_active = 1 AND rb.status = 'confirmed'
    AND date(rb.start_month) <= mm.month_first
    AND (rb.end_month IS NULL OR date(rb.end_month) >= mm.month_first)
  WHERE NOT EXISTS (SELECT 1 FROM invoices iv
                    WHERE iv.recurring_billing_id = rb.id
                      AND date(iv.billing_month) = mm.month_first)
),
ap_now AS (
  SELECT
    COALESCE((SELECT SUM(-ob.balance)
              FROM opening_balances ob JOIN accounts a ON a.id = ob.account_id
              WHERE a.code = '2020' AND ob.fiscal_year_id IN (SELECT id FROM cur_yr)), 0)
    +
    COALESCE((SELECT SUM(CASE WHEN l.dc = 'C' THEN l.amount ELSE -l.amount END)
              FROM journal_lines l
              JOIN journal_entries e ON e.id = l.journal_entry_id
              JOIN accounts a ON a.id = l.account_id
              WHERE e.status = 'posted' AND a.code = '2020'
                AND e.fiscal_year_id IN (SELECT id FROM cur_yr)), 0) AS ap
),
exp_now AS (
  SELECT COALESCE(SUM(amount), 0) AS exp
  FROM expense_request
  WHERE settlement_status = 'approved'
),
vend_out AS (
  SELECT max(COALESCE(date(v.due_date, 'start of month'),
                      (SELECT month_first FROM months WHERE idx = 0)),
             (SELECT month_first FROM months WHERE idx = 0)) AS m,
         v.amount AS amt
  FROM vendor_invoices v
  WHERE v.status IN ('received', 'accrued')
),
sal_out AS (
  SELECT mm.month_first AS m, SUM(ms.cost) AS amt
  FROM months mm
  JOIN fiscal_periods fp ON date(fp.start_date) = mm.month_first
  JOIN monthly_salaries ms ON ms.fiscal_year_id = fp.fiscal_year_id AND ms.period_no = fp.period_no
  GROUP BY mm.month_first
),
flows AS (
  SELECT mm.idx, mm.month_first,
    COALESCE((SELECT SUM(amt) FROM inv_in WHERE inv_in.m = mm.month_first AND amt > 0), 0)
    + COALESCE((SELECT SUM(amt) FROM rec_in WHERE rec_in.m = mm.month_first), 0) AS cash_in,
    (CASE WHEN mm.idx = 0 THEN (SELECT ap FROM ap_now) + (SELECT exp FROM exp_now) ELSE 0 END)
    + COALESCE((SELECT SUM(amt) FROM vend_out WHERE vend_out.m = mm.month_first), 0)
    + COALESCE((SELECT amt FROM sal_out WHERE sal_out.m = mm.month_first), 0) AS cash_out
  FROM months mm
),
cash_final AS (
  SELECT idx, cash_in, cash_out,
    (SELECT cash FROM cash_now)
      + SUM(cash_in - cash_out) OVER (ORDER BY idx ROWS UNBOUNDED PRECEDING) AS ending
  FROM flows
),
alert_rate AS (
  -- 既定値のフォールバックが要る。マスタの行が消えると rate が NULL になり、
  -- 下の比較が NULL → budget_alert が 0 行 → ポータルの予算警告が行ごと消える。
  -- 「警告が無い＝健全」に見えるので気づけない。同ファイルの PAY_DUE_SOON_DAYS は
  -- 既に COALESCE を持っており、作法が割れていた（既定 80% は投入値と同じ）
  SELECT COALESCE((SELECT amount FROM system_thresholds WHERE code = 'BUDGET_ALERT_RATE' LIMIT 1), 80) AS rate
),
budget_alert AS (
  SELECT b.department_id AS department_id
  FROM (SELECT department_id, account_id, SUM(amount) AS budget
        FROM budget_lines
        WHERE fiscal_year_id IN (SELECT id FROM cur_yr)
        GROUP BY department_id, account_id) b
  LEFT JOIN (SELECT l.department_id, l.account_id,
                    SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS actual
             FROM journal_lines l
             JOIN journal_entries e ON e.id = l.journal_entry_id
             JOIN accounts a ON a.id = l.account_id
             WHERE e.status = 'posted'
               AND e.fiscal_year_id IN (SELECT id FROM cur_yr)
               AND a.account_type = 'expense'
             GROUP BY l.department_id, l.account_id) act
    ON act.department_id IS b.department_id AND act.account_id = b.account_id
  WHERE b.budget > 0
    AND COALESCE(act.actual, 0) * 100 / b.budget >= (SELECT rate FROM alert_rate)
  GROUP BY b.department_id
)
SELECT
  (SELECT count(*) FROM pay WHERE days_left < 0) AS pay_overdue,
  (SELECT count(*) FROM pay
    WHERE days_left >= 0 AND days_left <= (SELECT days FROM threshold)) AS pay_soon,
  (SELECT c FROM recv) AS receivable_overdue,
  -- 資金ショート（期末資金がマイナス）の月数。**危険水域とは混ぜない**——
  -- 混ぜるとポータルが黒字の月まで「ショート」と表示し、予測画面の「△ 危険水域」と重大度が食い違う
  (SELECT count(*) FROM cash_final WHERE ending < 0) AS cash_alert_months,
  -- 危険水域（マイナスではないが閾値を下回る）の月数（BUG-0249）。
  -- **CashFlowForecastData.Query.sql の alert_mark と同じ条件にすること**（この 2 本は複製・BUG-0257）。
  -- 閾値が 0／未設定なら 0 件になり、従来どおり「マイナスのときだけ」の挙動に戻る
  (SELECT count(*) FROM cash_final
    WHERE ending >= 0
      AND COALESCE((SELECT amount FROM system_thresholds WHERE code = 'CASH_ALERT_BALANCE' LIMIT 1), 0) > 0
      AND ending < (SELECT amount FROM system_thresholds WHERE code = 'CASH_ALERT_BALANCE' LIMIT 1)
  ) AS cash_warn_months,
  (SELECT count(*) FROM budget_alert) AS budget_alert_depts,
  -- 警告が出ている部門の ID リスト（カンマ区切り。非経理ユーザーの「自部門のみ表示」判定用・2026-08-06）
  (SELECT COALESCE(group_concat(department_id), '') FROM budget_alert) AS budget_alert_dept_ids,
  (SELECT days FROM threshold) AS due_soon_days
