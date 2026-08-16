-- 何を保証するか: 総勘定元帳の売掛金(1100)残高と、売掛残高一覧（請求書ベース）の残額合計が一致すること。
-- 違反時の意味: 帳簿（仕訳）と業務データ（請求書・入金）が乖離している。
--               どちらかだけを直した、仕訳を伴わない状態変更をした、といったバグの決定的な証拠になる。
--               試算表と元帳は同じ仕訳から導出されるため一致は自明だが、この「元帳 × 業務一覧」の突合は
--               自明ではなく、会計アプリで最も価値のあるクロスチェック。
-- 出典: docs/tests/11_E2Eテストシナリオ/README.md §5 ③（売掛金元帳残高 = 売掛残高一覧の残額合計）
--       Modules/Sales/ReceivableBalance.Query.sql（残額の定義。status='paid' は残額 0）
-- 実装メモ: 元帳残高は「期首残高を持つ最も古い年度の期首 + それ以降の全確定仕訳」で計算する
--           （年度ごとの期首を全部足すと過年度の動きを二重計上するため）。
WITH origin AS (
  SELECT fy.id AS fy_id, date(fy.start_date) AS sd
  FROM fiscal_years fy
  WHERE EXISTS (SELECT 1 FROM opening_balances ob WHERE ob.fiscal_year_id = fy.id)
  ORDER BY date(fy.start_date)
  LIMIT 1
),
ledger AS (
  SELECT
    COALESCE((SELECT SUM(ob.balance) FROM opening_balances ob
              JOIN accounts a ON a.id = ob.account_id
              WHERE ob.fiscal_year_id = (SELECT fy_id FROM origin) AND a.code = '1100'), 0)
    + COALESCE((SELECT SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END)
                FROM journal_lines l
                JOIN journal_entries e ON e.id = l.journal_entry_id
                JOIN accounts a ON a.id = l.account_id
                WHERE e.status = 'posted' AND a.code = '1100'
                  AND date(e.entry_date) >= (SELECT sd FROM origin)), 0) AS bal
),
settled AS (
  SELECT r.invoice_id AS inv, SUM(r.amount) AS received
  FROM receipts r
  WHERE EXISTS (SELECT 1 FROM journal_entries je
                WHERE je.source_type = 'receipt' AND je.source_id = r.id)
  GROUP BY r.invoice_id
),
listed AS (
  SELECT SUM(CASE WHEN i.status = 'paid' THEN 0
                  ELSE COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0)
                       - COALESCE(s.received, 0) END) AS bal
  FROM invoices i
  LEFT JOIN settled s ON s.inv = i.id
  WHERE i.status <> 'void' AND i.status <> 'draft'
),
cmp AS (
  SELECT (SELECT bal FROM ledger) AS 売掛金元帳残高,
         COALESCE((SELECT bal FROM listed), 0) AS 売掛残高一覧合計
)
SELECT 売掛金元帳残高, 売掛残高一覧合計,
       売掛金元帳残高 - 売掛残高一覧合計 AS 差額
FROM cmp
WHERE 売掛金元帳残高 <> 売掛残高一覧合計
