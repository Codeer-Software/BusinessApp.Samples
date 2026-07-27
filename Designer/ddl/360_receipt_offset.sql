-- 360_receipt_offset.sql — 相殺入金の本格対応（ADR-0035、AccountingSQLite）
-- 1) receipts に相殺相手の仕入先請求リンクを追加
-- 2) vendor_invoices に select_label（番号 取引先名 摘要）を追加し、トリガーで保守
--    （設計判断は 280_select_labels.sql と同じ: 経路を問わず整合させるため DB トリガー）

ALTER TABLE receipts ADD COLUMN offset_vendor_invoice_id INTEGER REFERENCES vendor_invoices(id);

ALTER TABLE vendor_invoices ADD COLUMN select_label TEXT;

CREATE TRIGGER IF NOT EXISTS trg_vendor_invoices_label_ins AFTER INSERT ON vendor_invoices
BEGIN
  UPDATE vendor_invoices SET select_label =
    COALESCE(new.invoice_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.description, '')
  WHERE id = new.id;
END;

CREATE TRIGGER IF NOT EXISTS trg_vendor_invoices_label_upd AFTER UPDATE OF invoice_no, partner_id, description ON vendor_invoices
BEGIN
  UPDATE vendor_invoices SET select_label =
    COALESCE(new.invoice_no, '') || ' ' ||
    COALESCE((SELECT name FROM partners WHERE id = new.partner_id), '') || ' ' ||
    COALESCE(new.description, '')
  WHERE id = new.id;
END;

-- 取引先名の変更に追随（280 の trg_partners_name_label は vendor_invoices を知らないため別トリガーで追加）
CREATE TRIGGER IF NOT EXISTS trg_partners_name_label_vi AFTER UPDATE OF name ON partners
BEGIN
  UPDATE vendor_invoices SET select_label =
    COALESCE(invoice_no, '') || ' ' || new.name || ' ' || COALESCE(description, '')
  WHERE partner_id = new.id;
END;

-- 既存データのバックフィル
UPDATE vendor_invoices SET select_label =
  COALESCE(invoice_no, '') || ' ' ||
  COALESCE((SELECT name FROM partners WHERE id = vendor_invoices.partner_id), '') || ' ' ||
  COALESCE(description, '');
