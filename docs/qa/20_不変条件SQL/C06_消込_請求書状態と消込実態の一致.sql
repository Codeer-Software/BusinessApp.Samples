-- 何を保証するか: 請求書の status が、実際に売掛金が消し込まれた額と一致していること。
--   ・status='paid'    なのに売掛金が全額消し込まれていない
--   ・status='issued'  なのに売掛金の消込が始まっている
--   ・status='partial' なのに消込 0、または全額消込済み
--   ・status='void'    なのに消込がある（取消したのに入金が生きている）
-- 違反時の意味: 「消込フラグ（status）」と「消込仕訳の実体」という二重の真実がズレている。
--               売掛残高一覧・入金予定・資金繰り予測が、status を見る箇所と仕訳を見る箇所で
--               別の答えを出す。ADR-0051 が抱える弱点が現実化した状態。
-- 出典: ADR-0051（入金の集計は消込仕訳がある行だけ）
--       Modules/Sales/ReceivableBalance.Query.sql（status='paid' は残額 0 として特別扱いされる）
-- 実装メモ: 「消込済み額」は receipts.amount ではなく、消込仕訳が売掛金(1100)を貸方で減らした額で測る。
--           振込手数料を当社が負担する入金は 入金額 < 請求額 でも債権は全額消えるため、
--           receipts.amount で判定すると正常な取引が違反に見える（実際に第18期のデータで発生する）。
-- 入金は 1 件で複数の請求書に消し込める（ADR-0071）。売掛金の貸方行は請求書ごとに分かれて
-- 立つので、**消込明細の請求書と金額**で按分して数える（合算入金でも請求書単位で測れる）。
WITH cleared AS (
  SELECT rl.invoice_id AS inv,
         SUM(CASE WHEN rtot.total > 0
                  THEN CAST(jetot.cr * rl.amount / rtot.total AS INTEGER)
                  ELSE 0 END) AS cleared
  FROM receipt_lines rl
  JOIN receipts r ON r.id = rl.receipt_id
  JOIN (SELECT receipt_id, SUM(amount) AS total FROM receipt_lines GROUP BY receipt_id) rtot
    ON rtot.receipt_id = rl.receipt_id
  JOIN (SELECT je.source_id AS rid,
               SUM(COALESCE((SELECT SUM(jl.amount) FROM journal_lines jl
                             JOIN accounts a ON a.id = jl.account_id
                             WHERE jl.journal_entry_id = je.id AND jl.dc = 'C' AND a.code = '1100'), 0)) AS cr
        FROM journal_entries je
        WHERE je.source_type = 'receipt'
        GROUP BY je.source_id) jetot
    ON jetot.rid = r.id
  GROUP BY rl.invoice_id
),
base AS (
  SELECT i.id, i.invoice_no, i.status, i.issue_date,
         COALESCE(i.amount, 0) + COALESCE(i.tax_amount, 0) AS total,
         COALESCE(c.cleared, 0) AS cleared
  FROM invoices i
  LEFT JOIN cleared c ON c.inv = i.id
)
SELECT
    CASE
      WHEN status = 'paid'    AND cleared < total THEN '入金済だが売掛金が消えていない'
      WHEN status = 'issued'  AND cleared >= total AND total > 0 THEN '全額消込済みなのに未入金状態'
      WHEN status = 'issued'  AND cleared > 0 THEN '一部消込があるのに未入金状態'
      WHEN status = 'partial' AND cleared <= 0 THEN '一部入金だが消込が無い'
      WHEN status = 'partial' AND cleared >= total AND total > 0 THEN '一部入金だが全額消込済み'
      WHEN status = 'void'    AND cleared > 0 THEN '取消済みなのに消込がある'
    END AS 違反,
    id AS 請求書id, invoice_no AS 請求書番号, status AS 状態,
    issue_date AS 発行日, total AS 税込請求額, cleared AS 消込額,
    total - cleared AS 残額
FROM base
WHERE (status = 'paid'    AND cleared < total)
   OR (status = 'issued'  AND cleared > 0)
   OR (status = 'partial' AND (cleared <= 0 OR (cleared >= total AND total > 0)))
   OR (status = 'void'    AND cleared > 0)
ORDER BY id
