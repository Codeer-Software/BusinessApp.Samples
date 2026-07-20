-- 300: 検収の合算先請求書参照（docs/issues/ISSUE-0002 の合算請求改善）
-- 手動請求書に複数検収をまとめた場合に、検収側から「どの請求書に含めたか」を記録する。
-- NULL 許可の追加列のみ（既存データ・既存フローに影響なし）。
ALTER TABLE acceptances ADD COLUMN billed_invoice_id INTEGER REFERENCES invoices(id);
