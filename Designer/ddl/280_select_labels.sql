-- 280_select_labels.sql — 参照ドロップダウンの表示ラベル（U4-3 / レビュー第4弾、BusinessAppSQLite）
-- 見積/受注/検収/請求書の参照 Select が番号のみで判別しづらいため、
-- 「番号 取引先名 件名」を select_label 列に持たせ、参照側の DisplayTextVariable で表示する。
--
-- 保守はアプリ層ではなく DB トリガーで行う（設計判断）:
--   請求書は 手動作成 / 検収から作成 / SES一括生成 / 定期請求実行 の4経路で INSERT される。
--   mod.cs のフックだと経路ごとに保守が必要で漏れやすい。トリガーなら経路を問わず常に整合する。
--   取引先名・受注件名の変更にも連動トリガーで追随する。

ALTER TABLE quotes       ADD COLUMN select_label TEXT;
ALTER TABLE sales_orders ADD COLUMN select_label TEXT;
ALTER TABLE acceptances  ADD COLUMN select_label TEXT;
ALTER TABLE invoices     ADD COLUMN select_label TEXT;

-- ---- quotes ----
CREATE TRIGGER IF NOT EXISTS trg_quotes_label_ins AFTER INSERT ON quotes
BEGIN
  UPDATE quotes SET select_label =
    COALESCE(new.quote_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.title, '')
  WHERE id = new.id;
END;
CREATE TRIGGER IF NOT EXISTS trg_quotes_label_upd AFTER UPDATE OF quote_no, partner_id, title ON quotes
BEGIN
  UPDATE quotes SET select_label =
    COALESCE(new.quote_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.title, '')
  WHERE id = new.id;
END;

-- ---- sales_orders（自身のラベル＋配下の検収ラベルも更新） ----
CREATE TRIGGER IF NOT EXISTS trg_sales_orders_label_ins AFTER INSERT ON sales_orders
BEGIN
  UPDATE sales_orders SET select_label =
    COALESCE(new.order_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.title, '')
  WHERE id = new.id;
END;
CREATE TRIGGER IF NOT EXISTS trg_sales_orders_label_upd AFTER UPDATE OF order_no, partner_id, title ON sales_orders
BEGIN
  UPDATE sales_orders SET select_label =
    COALESCE(new.order_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.title, '')
  WHERE id = new.id;
  UPDATE acceptances SET select_label =
    COALESCE(acceptance_no, '') || ' ' ||
    COALESCE((SELECT p.name FROM partners p WHERE p.id = new.partner_id), '') || ' ' ||
    COALESCE(new.title, '')
  WHERE sales_order_id = new.id;
END;

-- ---- acceptances（取引先・件名は受注から引く） ----
CREATE TRIGGER IF NOT EXISTS trg_acceptances_label_ins AFTER INSERT ON acceptances
BEGIN
  UPDATE acceptances SET select_label =
    COALESCE(new.acceptance_no, '') || ' ' ||
    COALESCE((SELECT p.name FROM sales_orders so JOIN partners p ON p.id = so.partner_id
              WHERE so.id = new.sales_order_id), '') || ' ' ||
    COALESCE((SELECT so.title FROM sales_orders so WHERE so.id = new.sales_order_id), '')
  WHERE id = new.id;
END;
CREATE TRIGGER IF NOT EXISTS trg_acceptances_label_upd AFTER UPDATE OF acceptance_no, sales_order_id ON acceptances
BEGIN
  UPDATE acceptances SET select_label =
    COALESCE(new.acceptance_no, '') || ' ' ||
    COALESCE((SELECT p.name FROM sales_orders so JOIN partners p ON p.id = so.partner_id
              WHERE so.id = new.sales_order_id), '') || ' ' ||
    COALESCE((SELECT so.title FROM sales_orders so WHERE so.id = new.sales_order_id), '')
  WHERE id = new.id;
END;

-- ---- invoices ----
CREATE TRIGGER IF NOT EXISTS trg_invoices_label_ins AFTER INSERT ON invoices
BEGIN
  UPDATE invoices SET select_label =
    COALESCE(new.invoice_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.title, '')
  WHERE id = new.id;
END;
CREATE TRIGGER IF NOT EXISTS trg_invoices_label_upd AFTER UPDATE OF invoice_no, partner_id, title ON invoices
BEGIN
  UPDATE invoices SET select_label =
    COALESCE(new.invoice_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.title, '')
  WHERE id = new.id;
END;

-- ---- 取引先名の変更に追随 ----
CREATE TRIGGER IF NOT EXISTS trg_partners_name_label AFTER UPDATE OF name ON partners
BEGIN
  UPDATE quotes SET select_label =
    COALESCE(quote_no, '') || ' ' || new.name || ' ' || COALESCE(title, '')
  WHERE partner_id = new.id;
  UPDATE sales_orders SET select_label =
    COALESCE(order_no, '') || ' ' || new.name || ' ' || COALESCE(title, '')
  WHERE partner_id = new.id;
  UPDATE acceptances SET select_label =
    COALESCE(acceptance_no, '') || ' ' || new.name || ' ' ||
    COALESCE((SELECT so.title FROM sales_orders so WHERE so.id = acceptances.sales_order_id), '')
  WHERE sales_order_id IN (SELECT id FROM sales_orders WHERE partner_id = new.id);
  UPDATE invoices SET select_label =
    COALESCE(invoice_no, '') || ' ' || new.name || ' ' || COALESCE(title, '')
  WHERE partner_id = new.id;
END;

-- ---- 既存データのバックフィル ----
UPDATE quotes SET select_label =
  COALESCE(quote_no, '') || ' ' ||
  COALESCE((SELECT name FROM partners WHERE id = quotes.partner_id), '') || ' ' ||
  COALESCE(title, '');
UPDATE sales_orders SET select_label =
  COALESCE(order_no, '') || ' ' ||
  COALESCE((SELECT name FROM partners WHERE id = sales_orders.partner_id), '') || ' ' ||
  COALESCE(title, '');
UPDATE acceptances SET select_label =
  COALESCE(acceptance_no, '') || ' ' ||
  COALESCE((SELECT p.name FROM sales_orders so JOIN partners p ON p.id = so.partner_id
            WHERE so.id = acceptances.sales_order_id), '') || ' ' ||
  COALESCE((SELECT so.title FROM sales_orders so WHERE so.id = acceptances.sales_order_id), '');
UPDATE invoices SET select_label =
  COALESCE(invoice_no, '') || ' ' ||
  COALESCE((SELECT name FROM partners WHERE id = invoices.partner_id), '') || ' ' ||
  COALESCE(title, '');
