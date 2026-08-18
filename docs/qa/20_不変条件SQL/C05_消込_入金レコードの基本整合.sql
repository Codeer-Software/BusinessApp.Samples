-- 何を保証するか: 入金レコードの基本形が保たれていること。
--   (a) 入金額が正の値（NULL・0・マイナスの入金を作らない）
--   (b) 請求書に紐づいている（宛先不明の入金を作らない）
--   (c) 相殺入金（method='offset'）は必ず相殺先の仕入先請求書を持つ
-- 違反時の意味: 消込集計が壊れる。特にマイナス入金は「返金」を入金テーブルで表現した回避策の痕跡で、
--               残高計算・入金予定・資金繰り予測のすべてで符号事故を起こす。
-- 出典: ADR-0035（相殺は買掛側連動＋取消一本化）／ADR-0051（消込済みのみ集計）
SELECT '入金額が正でない' AS 違反, r.id AS 入金id, r.receipt_date AS 入金日,
       r.invoice_id AS 請求書id, r.method AS 方法, CAST(r.amount AS TEXT) AS 値
FROM receipts r
WHERE r.amount IS NULL OR r.amount <= 0

UNION ALL
-- 消込明細（receipt_lines）が入金の正。ヘッダの invoice_id は移行の名残で使わない（ADR-0071）
SELECT '消込明細が無い', r.id, r.receipt_date, NULL, r.method, NULL
FROM receipts r
WHERE NOT EXISTS (SELECT 1 FROM receipt_lines rl WHERE rl.receipt_id = r.id)

UNION ALL
SELECT '消込明細の合計が入金額と一致しない', r.id, r.receipt_date, NULL, r.method,
       CAST(COALESCE(r.amount, 0) - (SELECT SUM(rl.amount) FROM receipt_lines rl
                                     WHERE rl.receipt_id = r.id) AS TEXT)
FROM receipts r
WHERE EXISTS (SELECT 1 FROM receipt_lines rl WHERE rl.receipt_id = r.id)
  AND COALESCE(r.amount, 0) <> (SELECT SUM(rl.amount) FROM receipt_lines rl WHERE rl.receipt_id = r.id)

UNION ALL
SELECT '消込明細の金額が正でない', r.id, r.receipt_date, rl.invoice_id, r.method, CAST(rl.amount AS TEXT)
FROM receipt_lines rl JOIN receipts r ON r.id = rl.receipt_id
WHERE rl.amount IS NULL OR rl.amount <= 0

UNION ALL
-- 1 入金の明細は同一取引先の請求書に限る（ADR-0071。売掛金の補助元帳が取引先単位で合わなくなる）
SELECT '1 入金の明細が複数の取引先にまたがる', r.id, r.receipt_date, NULL, r.method,
       CAST((SELECT COUNT(DISTINCT i.partner_id) FROM receipt_lines rl2
             JOIN invoices i ON i.id = rl2.invoice_id
             WHERE rl2.receipt_id = r.id) AS TEXT)
FROM receipts r
WHERE (SELECT COUNT(DISTINCT i.partner_id) FROM receipt_lines rl2
       JOIN invoices i ON i.id = rl2.invoice_id
       WHERE rl2.receipt_id = r.id) > 1

UNION ALL
SELECT '相殺入金に相殺先が無い', r.id, r.receipt_date, r.invoice_id, r.method, NULL
FROM receipts r
WHERE r.method = 'offset' AND r.offset_vendor_invoice_id IS NULL
