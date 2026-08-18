-- 730: 伝票に「対象の固定資産」を持たせる（BUG-0340 / ADR-0073）
--
-- 償却累計の集計が `source_type='depreciation' AND source_id=資産` の伝票しか見ておらず、
-- **アプリ自身が案内している訂正手段**（償却生成の確認ダイアログ「誤りは振替伝票側で訂正してください」）で
-- 償却を戻すと、その伝票は連動元が違うので無視されていた。簿価が過小のまま処分され、
-- 除却損・売却損益がその分ずれる。
--
-- 根本は「仕訳が固定資産を指していない」こと。指させる。
--   * 明細行ではなく**伝票ヘッダ**に持たせる。訂正伝票は 1 枚 1 資産が実務の形で、
--     明細行に列を足すと仕訳入力の格子が 10 列になり日常の入力が重くなる。
--     2 資産を直したいときは伝票を 2 枚に分ける（画面のヒントで案内する）。
--   * 自動生成（償却・処分）にも同じ列を埋める。以後の集計は連動元ではなく**この列**で引ける。
ALTER TABLE journal_entries ADD COLUMN fixed_asset_id INTEGER REFERENCES fixed_assets(id);

-- 既存の自動生成分を埋める（連動元から引き当てる）
UPDATE journal_entries
SET fixed_asset_id = source_id
WHERE fixed_asset_id IS NULL
  AND source_type IN ('depreciation', 'disposal')
  AND source_id IS NOT NULL
  AND EXISTS (SELECT 1 FROM fixed_assets fa WHERE fa.id = journal_entries.source_id);

CREATE INDEX IF NOT EXISTS idx_journal_entries_fixed_asset ON journal_entries(fixed_asset_id);
