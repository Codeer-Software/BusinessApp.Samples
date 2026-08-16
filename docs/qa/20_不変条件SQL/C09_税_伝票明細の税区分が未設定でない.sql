-- 何を保証するか: 見積・受注・検収・請求の各明細行に税区分が設定されていること。
-- 違反時の意味: 税額計算がスクリプトの既定（標準税率の課税売上とみなす）に落ちる。
--               輸出免税・非課税の取引を明細で表現したつもりでも黙って課税されるし、
--               逆に将来「未設定は 0%」に変えると税額が静かに変わる。
--               ADR-0052 で journal_lines / accounts は NOT NULL 化したが、
--               売上サイドの明細テーブルは NULL 可のまま残っている（構造的な残債）。
-- 出典: ADR-0050（税は明細の税区分ベース）／ADR-0052（税区分 NULL の廃止）
--       Modules/Sales/Invoice.mod.cs CalcTaxByLine()「税区分が未設定の行は課税売上 10% とみなす」
SELECT 'quote_lines' AS テーブル, ql.id AS 行id, q.quote_no AS 伝票番号,
       ql.description AS 摘要, ql.amount AS 金額
FROM quote_lines ql JOIN quotes q ON q.id = ql.quote_id
WHERE ql.tax_category_id IS NULL

UNION ALL
SELECT 'sales_order_lines', sl.id, so.order_no, sl.description, sl.amount
FROM sales_order_lines sl JOIN sales_orders so ON so.id = sl.sales_order_id
WHERE sl.tax_category_id IS NULL

UNION ALL
SELECT 'acceptance_lines', al.id, ac.acceptance_no, al.description, al.amount
FROM acceptance_lines al JOIN acceptances ac ON ac.id = al.acceptance_id
WHERE al.tax_category_id IS NULL

UNION ALL
SELECT 'invoice_lines', il.id, iv.invoice_no, il.description, il.amount
FROM invoice_lines il JOIN invoices iv ON iv.id = il.invoice_id
WHERE il.tax_category_id IS NULL
