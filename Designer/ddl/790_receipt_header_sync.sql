-- 790: 消込明細を外したとき、入金ヘッダの請求書欄を残った明細に合わせる（BUG-0378）
--
-- 合算入金から 1 行だけ外す経路がある（`Invoice.DeletePendingReceipts`——請求書を取消／下書きに戻すと、
-- **入金ごと消さずにその行だけ外す**）。このとき `receipts.invoice_id` は**外した請求書を指したまま**になる。
-- 明細が 1 行に戻ると入金は「1 対 1 の従来経路」で処理されるので、
-- **残った明細の請求書ではなく、取消済みの請求書に対して消込仕訳が起票される**。
-- 取消済み（void）の請求書が入金済に戻り、帳簿と状態が矛盾する。
--
-- ヘッダ → 明細の同期は 780 で入れた。**明細 → ヘッダの向きが抜けていた**ので、ここで塞ぐ。
-- 発火するのは「外した行がちょうどヘッダの指していた請求書で、かつ明細がまだ残っている」ときだけ。
DROP TRIGGER IF EXISTS trg_receipt_header_sync_ad;
CREATE TRIGGER trg_receipt_header_sync_ad
AFTER DELETE ON receipt_lines
WHEN EXISTS (SELECT 1 FROM receipts r
             WHERE r.id = OLD.receipt_id AND r.invoice_id = OLD.invoice_id)
 AND EXISTS (SELECT 1 FROM receipt_lines rl WHERE rl.receipt_id = OLD.receipt_id)
BEGIN
  UPDATE receipts
  SET invoice_id = (SELECT rl.invoice_id FROM receipt_lines rl
                    WHERE rl.receipt_id = OLD.receipt_id
                    ORDER BY rl.line_no, rl.id LIMIT 1)
  WHERE id = OLD.receipt_id;
END;
