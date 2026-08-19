-- 815_evidence_files.sql — 仕訳と仕入先請求書に証憑（ファイル）を添付できるようにする（BUG-0006 / BUG-0023）
--
-- 電子帳簿保存法の電子取引データ保存では、**受領した請求書・領収書のデータをそのまま保存**し、
-- 取引年月日・取引金額・取引先で検索できることが求められる。
-- 経費申請は明細ごとに領収書を持てる（`expense_request_lines.receipt_file_*`）が、
-- **仕訳と仕入先請求書には添付する場所が無かった**——
-- 銀行振込で払った家賃や、メールで届いた PDF 請求書の置き場が無い。
--
-- CLB の FileField は 3 列（ファイル名・サイズ・GUID）で持つ。実体は `StorageName: Local`
-- （`LocalData` 配下）に GUID 名で保存される。既存の領収書と同じ持ち方に揃える。

ALTER TABLE journal_entries ADD COLUMN evidence_file_name TEXT;    -- 証憑（FileField 3 列）
ALTER TABLE journal_entries ADD COLUMN evidence_file_size INTEGER;
ALTER TABLE journal_entries ADD COLUMN evidence_file_guid TEXT;

ALTER TABLE vendor_invoices ADD COLUMN evidence_file_name TEXT;    -- 受領した請求書 PDF 等
ALTER TABLE vendor_invoices ADD COLUMN evidence_file_size INTEGER;
ALTER TABLE vendor_invoices ADD COLUMN evidence_file_guid TEXT;
