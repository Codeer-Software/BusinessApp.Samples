-- 684_fix_import_parent_line_no.sql — CSV 取込の税行に親行を当てる（BUG-0063 の残骸・2026-08-18）
--
-- `JournalImport` が税行に `parent_line_no` を設定していなかったため、取込で作られた伝票の税行は
-- `is_tax_line=1` なのに親が NULL だった（不変条件 A09）。コード側は税区分の継承元にした
-- 本体行を親として記録するよう直した。ここでは既存の残骸を同じ規則で埋める。
--
-- 【当てる規則】同じ伝票の中で、**税行でなく・税区分が同じ・貸借が同じ**本体行のうち行番号が最小のもの。
-- 候補が 1 つに定まらない伝票は触らない（黙って間違った親を当てるより、赤いまま残すほうがよい）。
UPDATE journal_lines
SET parent_line_no = (
  SELECT MIN(b.line_no) FROM journal_lines b
  WHERE b.journal_entry_id = journal_lines.journal_entry_id
    AND b.is_tax_line = 0
    AND b.tax_category_id = journal_lines.tax_category_id
    AND b.dc = journal_lines.dc
)
WHERE is_tax_line = 1
  AND parent_line_no IS NULL
  AND (
    SELECT COUNT(DISTINCT b.tax_category_id) FROM journal_lines b
    WHERE b.journal_entry_id = journal_lines.journal_entry_id
      AND b.is_tax_line = 0
      AND b.dc = journal_lines.dc
  ) = 1
  AND EXISTS (
    SELECT 1 FROM journal_lines b
    WHERE b.journal_entry_id = journal_lines.journal_entry_id
      AND b.is_tax_line = 0
      AND b.tax_category_id = journal_lines.tax_category_id
      AND b.dc = journal_lines.dc
  );
