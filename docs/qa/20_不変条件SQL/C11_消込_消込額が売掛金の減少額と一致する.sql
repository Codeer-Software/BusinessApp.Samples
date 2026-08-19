-- 何を保証するか: 消込仕訳が売掛金を落とした額 ＝ `SUM(receipt_lines.amount + diff_amount)` であること。
-- 違反時の意味: BUG-0422 の再発。振込差額を当社負担で自動処理すると売掛は「入金額＋差額」だけ落ちるのに、
--               明細には入金額しか入らない。「いくら消し込んだか」の正典である
--               `v_invoice_received` と `SumReceipts()` は明細しか見ないので、
--               **消込済み額が差額ぶん過少に記録される**。
--               表面化するのは取消経路——取り消すと残額 440 円の幽霊予定が作られ、
--               そこへ正しい額を入れると**手数料がもう一度計上されて売掛金がマイナス**になる。
--               `status='paid'` の特別扱いが表向き隠すので、取消経路だけが素通しになる。
-- 出典: docs/qa/02_バグ台帳.md BUG-0422 ／ Designer/ddl/821・822
-- 備考: C05 は「明細合計＝ヘッダ入金額」（銀行に入ってきた額）を見る。こちらは**売掛側との突合**で、別物。

WITH ar AS (
  SELECT id FROM accounts WHERE account_role = 'accounts_receivable'
  UNION
  SELECT id FROM accounts WHERE code = '1100'
),
je AS (
  SELECT e.source_id AS receipt_id, MIN(e.id) AS 伝票id,
         SUM(CASE WHEN jl.dc = 'C' AND jl.account_id IN (SELECT id FROM ar) THEN jl.amount
                  WHEN jl.dc = 'D' AND jl.account_id IN (SELECT id FROM ar) THEN -jl.amount
                  ELSE 0 END) AS 売掛減少額
  FROM journal_entries e
  JOIN journal_lines jl ON jl.journal_entry_id = e.id
  WHERE e.source_type = 'receipt' AND e.status = 'posted'
  GROUP BY e.source_id
),
rl AS (
  SELECT receipt_id, SUM(amount) AS 明細額, SUM(COALESCE(diff_amount, 0)) AS 差額
  FROM receipt_lines GROUP BY receipt_id
)
SELECT r.id AS 入金id, date(r.receipt_date) AS 入金日, r.method AS 方法, r.amount AS 入金額,
       rl.明細額, rl.差額, (rl.明細額 + rl.差額) AS 消込額, je.伝票id, je.売掛減少額,
       je.売掛減少額 - (rl.明細額 + rl.差額) AS 差
FROM receipts r
JOIN je ON je.receipt_id = r.id
LEFT JOIN rl ON rl.receipt_id = r.id
WHERE COALESCE(je.売掛減少額, 0) <> COALESCE(rl.明細額, 0) + COALESCE(rl.差額, 0)
ORDER BY r.receipt_date, r.id
