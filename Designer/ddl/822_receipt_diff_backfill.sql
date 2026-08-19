-- 822_receipt_diff_backfill.sql — 既存の差額処理済み入金に diff_amount を埋め戻す（BUG-0422）
--
-- `ddl/821` は列を足しただけで、**過去の入金を埋め戻していなかった**。
-- そのため「消込済み額」を数える `v_invoice_received` / `SumReceipts()` は過去分について依然として過少で、
-- その入金を取り消すと当のバグ（残額の幽霊予定 → 再入金で手数料が二重計上）がそのまま再現する。
--
-- 埋め戻しの根拠は**仕訳**にある。差額処理をした消込仕訳は売掛金を「入金額＋差額」だけ貸方に落としているので、
--   差額 ＝（その仕訳が落とした売掛金）−（receipt_lines.amount の合計）
-- で逆算できる。明細が 1 行の入金だけを対象にする（合算入金は差額処理の対象外＝ADR-0071）。
--
-- 何度流しても同じ結果になる（`diff_amount = 0` の行だけ触る）。

WITH ar AS (
  SELECT id FROM accounts WHERE account_role = 'accounts_receivable'
  UNION
  SELECT id FROM accounts WHERE code = '1100'
),
je AS (
  SELECT e.source_id AS receipt_id,
         SUM(CASE WHEN jl.dc = 'C' AND jl.account_id IN (SELECT id FROM ar) THEN jl.amount
                  WHEN jl.dc = 'D' AND jl.account_id IN (SELECT id FROM ar) THEN -jl.amount
                  ELSE 0 END) AS ar_down
  FROM journal_entries e
  JOIN journal_lines jl ON jl.journal_entry_id = e.id
  WHERE e.source_type = 'receipt' AND e.status = 'posted'
  GROUP BY e.source_id
),
one AS (
  -- 明細がちょうど 1 行の入金だけ（合算入金に差額処理は無い）
  SELECT rl.id AS line_id, rl.receipt_id, rl.amount
  FROM receipt_lines rl
  WHERE (SELECT COUNT(*) FROM receipt_lines x WHERE x.receipt_id = rl.receipt_id) = 1
),
fix AS (
  SELECT one.line_id, je.ar_down - one.amount AS diff
  FROM one JOIN je ON je.receipt_id = one.receipt_id
  WHERE je.ar_down - one.amount > 0
)
UPDATE receipt_lines
   SET diff_amount = (SELECT diff FROM fix WHERE fix.line_id = receipt_lines.id)
 WHERE COALESCE(diff_amount, 0) = 0
   AND id IN (SELECT line_id FROM fix);

SELECT id AS 明細id, receipt_id AS 入金id, amount AS 入金額, diff_amount AS 差額
FROM receipt_lines WHERE COALESCE(diff_amount, 0) <> 0 ORDER BY id;
