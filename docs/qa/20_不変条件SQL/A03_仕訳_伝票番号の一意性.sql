-- 何を保証するか: 伝票番号（journal_no）は会計年度の中で一意であること。
-- 違反時の意味: 採番ロジックの競合。伝票を番号で特定できず、監査証跡が破綻する。
--               本アプリの採番は「その年度の最大 journal_no + 1」を各起票経路が個別に行うため、
--               同時起票・スクリプトの取りこぼしで重複しうる（構造的なリスク箇所）。
-- 出典: docs/tests/11_E2Eテストシナリオ/README.md §5 ②（期待値 0）
--       docs/04_会計ドメイン設計.md §3（journal_no は年度内連番）
SELECT
    je.fiscal_year_id AS 年度id,
    je.journal_no     AS 伝票番号,
    COUNT(*)          AS 重複件数,
    GROUP_CONCAT(je.id) AS 伝票id一覧
FROM journal_entries je
WHERE je.journal_no IS NOT NULL
GROUP BY je.fiscal_year_id, je.journal_no
HAVING COUNT(*) > 1
ORDER BY je.fiscal_year_id, je.journal_no
