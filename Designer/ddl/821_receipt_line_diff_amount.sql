-- 821_receipt_line_diff_amount.sql — 振込差額で消し込んだ分を消込明細に残す（BUG-0422）
--
-- 差額処理（振込手数料を当社負担で自動計上する ADR-0035 の経路）をすると、
-- 消込仕訳は売掛金を **入金額 ＋ 差額** だけ貸方に落とす。請求書は全額消込＝`paid` になる。
-- ところが `receipt_lines.amount` にはヘッダの入金額しか入らない（トリガ 780 がヘッダ額を写す）。
--
-- 「いくら消し込んだか」を数えるのは `SumReceipts()` と `v_invoice_received` で、
-- どちらも `receipt_lines` しか見ないので、**消込済み額が差額ぶん過少に記録される**。
--
-- 表面化するのは取消経路:
--   請求 550,000 →①一部入金 100,000 →②残 450,000 を 449,450 で入金（差額 550 を手数料処理）
--   → ①を取り消すと消込済みは 449,450 しか見えず、**残額 100,550 円の入金予定**が作られる
--   → 正しい 100,000 を入れると再び差額 550 が出て**手数料が二重計上**、売掛金は ▲550 円
--
-- 直し方の選択:
--   (a) `receipt_lines.amount` に差額を含める → **却下**。不変条件 C05「明細合計＝入金額」が壊れる。
--       ヘッダの入金額は「銀行に入ってきた額」であるべきで、そこに手数料を混ぜると
--       銀行明細との突合が合わなくなる
--   (b) **差額を別の列に持つ** → 採用。「入金額」と「消し込んだ額」は別物だと素直に表現できる。
--       C05 は現状のまま通り、集計側だけが `amount + diff_amount` を見る

ALTER TABLE receipt_lines ADD COLUMN diff_amount INTEGER NOT NULL DEFAULT 0;
-- ↑ その明細で「振込差額として当社が負担し、売掛から落とした額」。通常は 0

-- 消込済み額は **入金額＋差額**。ここが集計の唯一の入口（6 か所の複製を畳んだ正典）
DROP VIEW IF EXISTS v_invoice_received;
CREATE VIEW v_invoice_received AS
SELECT rl.invoice_id                            AS invoice_id,
       SUM(rl.amount + COALESCE(rl.diff_amount, 0)) AS received
FROM receipt_lines rl
JOIN receipts r ON r.id = rl.receipt_id
WHERE EXISTS (SELECT 1 FROM journal_entries je
              WHERE je.source_type = 'receipt' AND je.source_id = r.id)
GROUP BY rl.invoice_id;

SELECT COUNT(*) AS receipt_lines, SUM(diff_amount) AS diff_total FROM receipt_lines;
