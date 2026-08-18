-- 660_fix_over_acceptance_invoices.sql — 検収額を超える請求書の残骸を掃除する（BUG-0058・開発者判断 2026-08-18）
--
-- 【背景】不変条件 C10（検収額を超える請求が無い）と C08（売掛金元帳＝売掛残高一覧）が
-- 2 件のデータで赤かった。どちらも **2026-08-11 以前の旧バグの残骸**である
-- （`Invoice.Lines_OnDataChanged` が「金額 = 数量 × 単価」を無条件に走らせ、
--  分割検収の請求額を受注全額に書き戻していた。修正は ADR-0049/0050＝2026-08-12）。
-- コードの穴は ADR-0067（検収に紐づく請求明細は読み取り専用）で 2026-08-18 に塞いだので、
-- 新しくこの状態を作ることはもうできない。残るデータだけをここで整える。
--
-- 【方針】**請求書を検収に合わせる**（検収を請求に合わせない）。
-- 売上は検収基準で計上済み（ADR-0008）で、仕訳はどちらも検収額どおり正しい。
-- 検収を書き換えると「実在しない検収」を作ることになる。
--
-- 【入金予定について】どちらの入金も**未消込（消込仕訳が無い＝発行時に自動作成された予定・ADR-0032）**
-- なので、請求額に合わせて金額を直す。消込済みなら触ってはいけない（実際の入金額が事実のため）。

-- ---- (1) INV-26-017（請求 2,000,000 > 検収 1,200,000。差 800,000 税抜） ----
-- 検収 A-26-009 の明細は「数量 1 × 単価 2,000,000 のうち 1,200,000 を検収」という分割検収。
-- 請求明細は検収明細の写しなので、数量・単価はそのままに金額だけ検収額へ戻す。
UPDATE invoice_lines SET amount = 1200000
WHERE id = 40 AND invoice_id = 29 AND acceptance_line_id = 15;

UPDATE invoices SET amount = 1200000, tax_amount = 120000
WHERE id = 29 AND invoice_no = 'INV-26-017';

UPDATE receipts SET amount = 1320000
WHERE id = 36 AND invoice_id = 29
  AND NOT EXISTS (SELECT 1 FROM journal_entries je WHERE je.source_type = 'receipt' AND je.source_id = receipts.id);

-- ---- (2) INV-26-010（合計は合っているが、明細が検収明細の写しになっていない） ----
-- 検収 A-26-005 は 2 明細（5,000 と 45,000）なのに、請求書は 1 行 50,000 が
-- 5,000 の検収明細に紐づいていた。合計は 50,000 で一致するので帳簿は狂っていないが、
-- 行単位では請求 50,000 > 検収 5,000 になり C10 が赤くなる。写しの形に直す。
UPDATE invoice_lines SET description = 'あ', qty = 1, unit_price = 5000, amount = 5000
WHERE id = 13 AND invoice_id = 12 AND acceptance_line_id = 6;

INSERT INTO invoice_lines (invoice_id, line_no, description, qty, unit_price, amount, tax_category_id, unit, acceptance_line_id)
SELECT 12, 2, 'い', 3, 15000, 45000, 1, '', 7
WHERE NOT EXISTS (SELECT 1 FROM invoice_lines WHERE invoice_id = 12 AND acceptance_line_id = 7);
