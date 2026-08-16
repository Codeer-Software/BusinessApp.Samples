-- 何を保証するか: journal_lines.department_id が必ず埋まっており、実在する「部」ノードを指すこと。
-- 違反時の意味: 部門別 PL・予算実績が集計から漏れる。課（section）を伝票に付けると
--               部単位の集計と二重管理になり、どちらが正か決まらなくなる。
-- 出典: ADR-0056（仕訳行の部門を NOT NULL 化）／ADR-0044（部課 2 階層）
--       docs/04_会計ドメイン設計.md §8「不変条件: 伝票・仕訳の部門は部ノード（node_type='dept'）のみ」
SELECT '部門が未設定' AS 違反, jl.id AS 行id, je.id AS 伝票id, je.entry_date AS 日付,
       NULL AS 部門id, NULL AS 部門名, NULL AS ノード種別
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE jl.department_id IS NULL

UNION ALL
SELECT '部門が実在しない', jl.id, je.id, je.entry_date, jl.department_id, NULL, NULL
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE jl.department_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM departments d WHERE d.id = jl.department_id)

UNION ALL
SELECT '部門が部ノードでない', jl.id, je.id, je.entry_date, d.id, d.name, d.node_type
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
JOIN departments d ON d.id = jl.department_id
WHERE d.node_type <> 'dept'
