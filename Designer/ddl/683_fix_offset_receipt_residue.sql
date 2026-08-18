-- 683_fix_offset_receipt_residue.sql — 「相殺先が無い相殺入金」の残骸を直す（BUG-0064・2026-08-18）
--
-- 相殺入金を取り消して残額の入金予定に戻すとき、`method='offset'` を戻していなかったため
-- `offset_vendor_invoice_id` が NULL のまま相殺を名乗る自己矛盾した行が残っていた
-- （不変条件 C05 が検出）。コード側は `Receipt.mod.cs` で既定（銀行振込）へ戻すよう直した。
-- ここでは既存の残骸を同じ規則で整える。**未消込の行だけ**を対象にする
-- （消込済みなら仕訳が相殺で立っているので、方法を書き換えてはいけない）。
UPDATE receipts
SET method = 'bank'
WHERE method = 'offset'
  AND offset_vendor_invoice_id IS NULL
  AND NOT EXISTS (SELECT 1 FROM journal_entries je
                  WHERE je.source_type = 'receipt' AND je.source_id = receipts.id);
