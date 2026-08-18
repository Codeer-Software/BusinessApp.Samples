-- 支払予定表（D-6）: 未払の仕入先請求書（received/accrued）を支払期限順に一覧
-- days_left = 支払期限までの日数（負=期限超過）。警告: 超過=⚠ / N日以内=まもなく
-- N は system_thresholds.PAY_DUE_SOON_DAYS（ADR-0045 でマスタ化。ポータルのアラート件数と共用）
WITH threshold AS (
  SELECT COALESCE((SELECT amount FROM v_system_threshold_current WHERE code = 'PAY_DUE_SOON_DAYS'), 7) AS days
),
pend AS (
  SELECT
    date(v.due_date) AS due_date,
    p.name AS partner_name,
    v.invoice_no,
    v.amount,
    CASE v.status WHEN 'received' THEN '受領' WHEN 'accrued' THEN '未払計上済' ELSE v.status END AS status_disp,
    CAST(julianday(date(v.due_date)) - julianday(date('now', 'localtime')) AS INTEGER) AS days_left
  FROM vendor_invoices v
  LEFT JOIN partners p ON p.id = v.partner_id
  WHERE v.status IN ('received', 'accrued')
)

SELECT
  -- due_date NULL でも sort_key が NULL にならないよう COALESCE（NULL 連結は全体が NULL になり並びが壊れる）
  '1-' || COALESCE(due_date, '9999-99-99') || '-' || COALESCE(invoice_no, '') AS sort_key,
  due_date,
  partner_name,
  invoice_no,
  amount,
  status_disp,
  days_left,
  CASE WHEN days_left < 0 THEN '⚠ 期限超過'
       WHEN days_left <= (SELECT days FROM threshold) THEN 'まもなく期限'
       ELSE '' END AS warn
FROM pend

UNION ALL
-- 合計行はクエリの 1 行なので、一覧のページャは合計行も 1 件として数える（BUG-0336）。
-- 合計行に明細件数を書き込んでおけば「(1-7 /7件)」の 7 が明細 6 件＋合計行だと読み手に分かる。
-- CLB のサマリー行 API（AddSummaryRow）は ListField 専用で、Query モジュールの一覧画面には使えない。
SELECT
  '9-ZZZZ',
  NULL,
  '合計（明細 ' || (SELECT COUNT(*) FROM pend) || ' 件）',
  NULL,
  COALESCE(SUM(amount), 0),
  NULL,
  NULL,
  NULL
FROM pend

ORDER BY sort_key
