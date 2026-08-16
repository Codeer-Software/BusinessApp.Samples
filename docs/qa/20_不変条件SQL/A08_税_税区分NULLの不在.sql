-- 何を保証するか: 税区分（tax_category_id）を NULL のまま持つ行が無いこと。
--   ・journal_lines.tax_category_id は NOT NULL（税と無関係な行は「対象外」を明示する）
--   ・accounts.default_tax_category_id も必須（BS 科目も「対象外」を明示する）
-- 違反時の意味: 消費税集計表が「区分不明」の塊を持ち、申告額の根拠が作れない。
--               NULL は「未入力」と「対象外」を区別できないため、本アプリは NULL を廃止した。
-- 出典: ADR-0052（docs/decisions/0052）／docs/04_会計ドメイン設計.md §2・§3
-- 備考: DB 制約（NOT NULL）が効いていれば恒久的に 0 件。制約が落ちた／新経路が NULL を書いた
--       ときにここで気づける。
SELECT 'journal_lines.tax_category_id' AS 対象, jl.id AS 行id,
       je.id AS 伝票id, je.entry_date AS 日付, je.description AS 摘要
FROM journal_lines jl
JOIN journal_entries je ON je.id = jl.journal_entry_id
WHERE jl.tax_category_id IS NULL

UNION ALL
SELECT 'accounts.default_tax_category_id', a.id, NULL, NULL, a.code || ' ' || a.name
FROM accounts a
WHERE a.default_tax_category_id IS NULL
