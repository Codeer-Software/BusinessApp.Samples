-- 340_recurring_status.sql — 定期請求契約の確定フロー＋見積からの変換（ユーザー承認 2026-07-23）
-- status: draft（下書き。誰でも作成・編集可）/ confirmed（確定済。経理が確定、実行対象）
-- 実行対象は「confirmed かつ is_active」（RecurringRun 側で絞る）。
-- quote_id: 見積→定期請求契約の変換の出自トレース（NULL=直接登録）。
ALTER TABLE recurring_billings ADD COLUMN status TEXT;
ALTER TABLE recurring_billings ADD COLUMN quote_id INTEGER REFERENCES quotes(id);

-- 既存契約はすべて確定済として移行（従来は登録=即実行対象だったため互換を保つ）
UPDATE recurring_billings SET status = 'confirmed' WHERE status IS NULL;
