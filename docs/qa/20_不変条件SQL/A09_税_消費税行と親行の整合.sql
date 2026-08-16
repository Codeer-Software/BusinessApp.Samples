-- 何を保証するか: システム生成の消費税行（is_tax_line = 1）が正しく親行に紐づいていること。
--   (a) parent_line_no が同じ伝票内の本体行（is_tax_line = 0）を指している
--   (b) 科目が仮払消費税(1900) / 仮受消費税(2200) のいずれか
--   (c) 税区分が親行と同じ（本体行と税行は同じ区分を持つ）
-- 違反時の意味: 孤児税行。元行を編集・削除したときの税行再生成に失敗している。
--               消費税集計表は「税行と parent の対応」で機械的に出すため、ここが崩れると税額が狂う。
-- 出典: docs/04_会計ドメイン設計.md §3.2（明示税行方式）／§9「税行の再生成（孤児税行を作らない）」
SELECT '親行が存在しない' AS 違反, jl.id AS 税行id, je.id AS 伝票id,
       je.entry_date AS 日付, jl.parent_line_no AS 親行no, jl.amount AS 金額
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE jl.is_tax_line = 1
  AND NOT EXISTS (
        SELECT 1 FROM journal_lines p
        WHERE p.journal_entry_id = jl.journal_entry_id
          AND p.line_no = jl.parent_line_no
          AND COALESCE(p.is_tax_line, 0) = 0)

UNION ALL
SELECT '科目が仮払/仮受消費税でない', jl.id, je.id, je.entry_date, jl.parent_line_no, jl.amount
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
JOIN accounts a ON a.id = jl.account_id
WHERE jl.is_tax_line = 1
  AND a.code NOT IN ('1900', '2200')

UNION ALL
SELECT '税区分が親行と不一致', jl.id, je.id, je.entry_date, jl.parent_line_no, jl.amount
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
JOIN journal_lines p ON p.journal_entry_id = jl.journal_entry_id
                    AND p.line_no = jl.parent_line_no
                    AND COALESCE(p.is_tax_line, 0) = 0
WHERE jl.is_tax_line = 1
  AND COALESCE(jl.tax_category_id, -1) <> COALESCE(p.tax_category_id, -1)
