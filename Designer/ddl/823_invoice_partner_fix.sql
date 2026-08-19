-- 823_invoice_partner_fix.sql — 請求先と検収の受注先が食い違っている請求書を直す（BUG-0452）
--
-- INV-26-010（id=12・タイトル「あいうえお」＝ 2026-07-18 の QA 残骸）は
-- **株式会社アルタイル商事（4）宛**なのに、紐づく検収 A-26-005 の受注先は
-- **グランメゾン印刷株式会社（6）**だった。請求書の検収選択は `Status='confirmed'` でしか絞っておらず、
-- 別会社の検収を選べてしまう（BUG-0452。スクリプト側にガードを入れて塞いだ）。
--
-- どちらが正かの判断:
--   ・金額が完全に一致する（50,000 ＋ 5,000 ＝ 55,000 = A-26-005 の検収額）→ **検収との紐づけが正しい**
--   ・売上仕訳 No.67（source=acceptance/6）は `partner_id = 6` で計上済み
--     → **帳簿はすでに 6 に債権を立てている**
--   ・入金 id=10 は未確定（消込仕訳なし）＝ まだ帳簿に影響していない
-- したがって**請求書の取引先を 6 に直す**のが、帳簿と請求を一致させる直し方。
--
-- 何度流しても同じ結果になる（食い違っている行だけ触る）。

UPDATE invoices
   SET partner_id = (SELECT so.partner_id FROM acceptances ac
                       JOIN sales_orders so ON so.id = ac.sales_order_id
                      WHERE ac.id = invoices.acceptance_id)
 WHERE acceptance_id IS NOT NULL
   AND COALESCE(partner_id, -1) <> COALESCE((SELECT so.partner_id FROM acceptances ac
                                               JOIN sales_orders so ON so.id = ac.sales_order_id
                                              WHERE ac.id = invoices.acceptance_id), -1);

SELECT iv.id, iv.invoice_no, iv.partner_id AS 請求先, so.partner_id AS 受注先
FROM invoices iv
JOIN acceptances ac ON ac.id = iv.acceptance_id
JOIN sales_orders so ON so.id = ac.sales_order_id
WHERE iv.acceptance_id IS NOT NULL;
