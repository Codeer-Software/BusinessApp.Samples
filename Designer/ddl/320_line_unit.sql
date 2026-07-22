-- 320_line_unit.sql — 販売明細に単位列（ユーザー要望 2026-07-23・AccountingSQLite）
-- 見積書・請求書の明細に「単位」（式・人月・か月・時間 等）を持たせる。
-- 明細は 見積→受注→請求書 とコピーで流れるため 3 テーブルすべてに追加する。
-- 既存行は NULL（帳票では空欄表示）。マスタ化はしない（自由入力テキスト）。
ALTER TABLE quote_lines ADD COLUMN unit TEXT;
ALTER TABLE sales_order_lines ADD COLUMN unit TEXT;
ALTER TABLE invoice_lines ADD COLUMN unit TEXT;
