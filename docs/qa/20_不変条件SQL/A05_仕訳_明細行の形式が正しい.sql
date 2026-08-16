-- 何を保証するか: 確定済み伝票の明細行が複式簿記の最低条件を満たすこと。
--   (a) 明細行が 1 行も無い伝票が無い
--   (b) 借方だけ／貸方だけの片肺伝票が無い
--   (c) amount は必ず正の整数（0 円行・マイナス行を作らない）
--   (d) dc は 'D' / 'C' のいずれか
--   (e) line_no が伝票内で一意（消費税行の親子解決 parent_line_no → line_no がこれに依存する。
--       重複すると A09 の突合が多重になり、税行がどの行に紐づくか決まらなくなる）
-- 違反時の意味: 貸借一致（A01）が偶然成立していても、行の意味が壊れている。
--               マイナス金額は「貸借どちらに立てるか」の情報を二重に持つため禁止（1行1側＋正数が本アプリの決定）。
-- 出典: docs/04_会計ドメイン設計.md §3.1（1行1側・正の整数 amount・全行 amount > 0）
SELECT '明細行が無い' AS 違反, je.id AS 伝票id, NULL AS 行id, je.entry_date AS 日付, je.description AS 摘要, NULL AS 値
FROM journal_entries je
WHERE je.status = 'posted'
  AND NOT EXISTS (SELECT 1 FROM journal_lines l WHERE l.journal_entry_id = je.id)

UNION ALL
SELECT '片側しか行が無い', je.id, NULL, je.entry_date, je.description,
       'D=' || SUM(CASE WHEN jl.dc = 'D' THEN 1 ELSE 0 END) ||
       ' C=' || SUM(CASE WHEN jl.dc = 'C' THEN 1 ELSE 0 END)
FROM journal_entries je
JOIN journal_lines jl ON jl.journal_entry_id = je.id
WHERE je.status = 'posted'
GROUP BY je.id
HAVING SUM(CASE WHEN jl.dc = 'D' THEN 1 ELSE 0 END) = 0
    OR SUM(CASE WHEN jl.dc = 'C' THEN 1 ELSE 0 END) = 0

UNION ALL
SELECT '金額が正でない', je.id, jl.id, je.entry_date, je.description, CAST(jl.amount AS TEXT)
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE je.status = 'posted'
  AND (jl.amount IS NULL OR jl.amount <= 0)

UNION ALL
SELECT '貸借区分が不正', je.id, jl.id, je.entry_date, je.description, COALESCE(jl.dc, '(NULL)')
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE je.status = 'posted'
  AND (jl.dc IS NULL OR jl.dc NOT IN ('D', 'C'))

UNION ALL
SELECT '行番号が伝票内で重複', je.id, NULL, je.entry_date, je.description,
       'line_no=' || COALESCE(CAST(jl.line_no AS TEXT), '(NULL)') || ' ×' || CAST(COUNT(*) AS TEXT)
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE je.status = 'posted'
GROUP BY jl.journal_entry_id, jl.line_no
HAVING COUNT(*) > 1
