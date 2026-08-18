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
  -- invoices.status は NOT NULL 制約が無い。素の <> だと status が NULL の請求書が
  -- 黙って集計から落ちて差額の原因が見えなくなるため COALESCE で拾う。
  WHERE COALESCE(i.status, '') NOT IN ('void', 'draft')
),
-- 【調整項目】売上は検収の確定で計上する（ADR-0008）ので、**検収済み・未請求**の期間は
-- 元帳に売掛金があるのに請求書がまだ無い。これは業務として正常な状態であり、
-- 一覧に出ないぶんを調整項目として足さないと、この検査は「請求書を作るまで毎回赤」になってしまう。
-- 対象は「確定済みで、void/draft でない請求書が 1 枚も紐づいていない検収」。
-- **売上仕訳が実際に立っている検収だけ**を数える。確定済みでも仕訳が無い検収は
-- 元帳に売掛金を作っていないので、調整項目に入れると今度は逆向きにずれる。
uninvoiced AS (
  SELECT COALESCE(SUM(COALESCE(a.amount, 0) + COALESCE(a.tax_amount, 0)), 0) AS bal
  FROM acceptances a
  WHERE a.status = 'confirmed'
    AND EXISTS (SELECT 1 FROM journal_entries je
                WHERE je.source_type = 'acceptance' AND je.source_id = a.id AND je.status = 'posted')
    -- **請求書が 1 枚も無い**検収に限る。void の請求書があるものは対象外——
    -- 請求書が一度でも作られたなら、その後の売掛金は請求書の一生（取消・貸倒れ・赤伝）で動く。
    -- 実例: A-26-002 は請求書 INV-26-005 が void だが、売掛金は貸倒れ処理の仕訳で消えている
    AND NOT EXISTS (SELECT 1 FROM invoices i WHERE i.acceptance_id = a.id)
),
cmp AS (
  SELECT (SELECT bal FROM ledger) AS 売掛金元帳残高,
         COALESCE((SELECT bal FROM listed), 0) AS 売掛残高一覧合計,
         COALESCE((SELECT bal FROM uninvoiced), 0) AS 検収済み未請求
)
SELECT 売掛金元帳残高, 売掛残高一覧合計, 検収済み未請求,
       売掛金元帳残高 - (売掛残高一覧合計 + 検収済み未請求) AS 差額
FROM cmp
WHERE 売掛金元帳残高 <> 売掛残高一覧合計 + 検収済み未請求
