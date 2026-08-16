-- 何を保証するか: 確定済み（posted）の伝票には必ず伝票番号（journal_no）が振られていること。
-- 違反時の意味: 帳票（仕訳帳）に番号なしの行が出る。採番のすり抜け。
-- 出典: docs/04_会計ドメイン設計.md §3（journal_no は確定時に採番。draft は NULL 可）
SELECT
    je.id             AS 伝票id,
    je.fiscal_year_id AS 年度id,
    je.entry_date     AS 日付,
    je.entry_type     AS 種別,
    je.source_type    AS 連動元,
    je.description    AS 摘要
FROM journal_entries je
WHERE je.status = 'posted'
  AND je.journal_no IS NULL
ORDER BY je.entry_date, je.id
