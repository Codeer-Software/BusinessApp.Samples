-- 資金繰り予測（当月含む今後4ヶ月）: 期首資金 / 入金予定 / 出金予定 / 期末資金 / 警告
-- 「今日」は既存帳票（売掛残高・元帳の既定値）に合わせ date('now') を使用。
-- 入金: 未回収請求書（期日月、期日超過は当月）＋ 定期請求の未生成将来分（対象月の翌月末入金）
--       未回収額の控除は消込済みの入金だけ。発行時に自動作成される未確定の入金予定（ADR-0032）を
--       引くと全請求書が残額 0 になり、入金予定が構造的に 0 円になる（改善候補 A-2）
-- 出金: 未払金残高（当月）＋ 承認済み未仕訳の経費（当月）＋ 月次人件費（各月）
--       ＋ 仕入先請求書の未払い分（D-6 連動。支払期限月・期限超過/期限なしは当月。
--         received/accrued を請求書ベースで拾うため買掛金 GL 残高は加算しない=二重計上回避）
WITH RECURSIVE months(idx, month_first) AS (
  SELECT 0, date('now', 'start of month')
  UNION ALL
  SELECT idx + 1, date(month_first, '+1 month') FROM months WHERE idx < 3
),
cur_yr AS (
  SELECT id FROM fiscal_years
  WHERE date(start_date) <= date('now') AND date(end_date) >= date('now')
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
  SELECT COALESCE((SELECT tr.rate_percent
                   FROM tax_categories tc JOIN tax_rates tr ON tr.id = tc.tax_rate_id
                   WHERE tc.default_for = 'sales' AND tc.is_active = 1), 0) AS pct
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
  JOIN recurring_billings rb ON rb.is_active = 1
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
final AS (
  SELECT idx, strftime('%Y-%m', month_first) AS month_label, cash_in, cash_out,
    (SELECT cash FROM cash_now)
      + SUM(cash_in - cash_out) OVER (ORDER BY idx ROWS UNBOUNDED PRECEDING) AS ending
  FROM flows
)
SELECT
  idx AS sort_no,
  month_label,
  ending - (cash_in - cash_out) AS opening_cash,
  cash_in,
  cash_out,
  ending AS ending_cash,
  CASE WHEN ending < 0 THEN '⚠ 資金ショート' ELSE '' END AS alert_mark
FROM final
ORDER BY idx
