-- 770: 入金を「1 入金 : n 消込明細」にする（BUG-0012 / ADR-0071）
--
-- 取引先は月末に複数の請求をまとめて 1 回で振り込んでくる。受託ソフトハウスでは日常的に起きるのに、
-- `receipts` は `invoice_id` を 1 本しか持てないので、**入金を請求書の数だけ分割して手入力する**しかない。
-- 銀行明細は 1 行なのに帳簿では n 行になり、残高照合で必ず突き合わせに詰まる。
--
-- ADR-0066（経費申請の明細行化）と同じ型で明細を足す。ヘッダの `amount` は意味だけを
-- 「その入金の合計額」に変え、既存の 1:1 入金は**明細 1 行として移行**する。
-- `receipts.invoice_id` は**残すが使わなくなる**（移行の余地を残すため削除しない）。

CREATE TABLE IF NOT EXISTS receipt_lines (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    receipt_id INTEGER NOT NULL REFERENCES receipts(id),
    line_no    INTEGER NOT NULL,
    invoice_id INTEGER NOT NULL REFERENCES invoices(id),
    amount     INTEGER NOT NULL          -- その請求書へ充当する額（税込）
);
CREATE INDEX IF NOT EXISTS idx_receipt_lines_receipt ON receipt_lines(receipt_id);
CREATE INDEX IF NOT EXISTS idx_receipt_lines_invoice ON receipt_lines(invoice_id);

-- 既存の 1:1 入金を明細 1 行として移行する（何度流しても増えないように NOT EXISTS で守る）
INSERT INTO receipt_lines (receipt_id, line_no, invoice_id, amount)
SELECT r.id, 1, r.invoice_id, COALESCE(r.amount, 0)
FROM receipts r
WHERE r.invoice_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM receipt_lines rl WHERE rl.receipt_id = r.id);

-- ---------------------------------------------------------------------------
-- 請求書ごとの「消込済み入金額」。**同じ式が 6 か所に複製されていた**ので 1 本に畳む
-- （売掛残高一覧・資金繰り予測・ポータルのアラート・不変条件 C04/C06/C08）。
-- 消込済み（消込仕訳がある）だけを数えるのは ADR-0051。未確定は「入金予定」であってまだ入金ではない。
-- ---------------------------------------------------------------------------
DROP VIEW IF EXISTS v_invoice_received;
CREATE VIEW v_invoice_received AS
SELECT rl.invoice_id     AS invoice_id,
       SUM(rl.amount)    AS received
FROM receipt_lines rl
JOIN receipts r ON r.id = rl.receipt_id
WHERE EXISTS (SELECT 1 FROM journal_entries je
              WHERE je.source_type = 'receipt' AND je.source_id = r.id)
GROUP BY rl.invoice_id;
