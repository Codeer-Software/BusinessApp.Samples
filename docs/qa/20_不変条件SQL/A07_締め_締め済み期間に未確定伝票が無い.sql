-- 何を保証するか: 締め済み（closed）の月次期間・会計年度に、下書き（draft）伝票が残っていないこと。
-- 違反時の意味: その伝票はもう確定できない（締め済み期間は保存拒否）ため、
--               永久に帳簿へ載らない「宙に浮いた取引」になる。月次締めの手順漏れ。
-- 出典: docs/04_会計ドメイン設計.md §6 月次締め 1.（draft 伝票が残っていれば警告）
--       同 §0-4（締め済み期間の仕訳は変更不可。訂正は赤黒）
-- 備考: 「締め後に伝票が追加・変更されたか」は DB からは判定できない（更新履歴を持たないため）。
--       ここで検出できるのは「締め済み期間に残った未確定伝票」だけ。
SELECT
    '締め済み月次期間の下書き' AS 違反,
    je.id           AS 伝票id,
    je.entry_date   AS 日付,
    fy.name         AS 年度,
    fp.period_no    AS 期,
    je.description  AS 摘要,
    je.source_type  AS 連動元
FROM journal_entries je
JOIN fiscal_periods fp ON date(je.entry_date) >= date(fp.start_date)
                      AND date(je.entry_date) <= date(fp.end_date)
JOIN fiscal_years fy ON fy.id = fp.fiscal_year_id
WHERE COALESCE(je.status, '') <> 'posted'   -- status が NULL の伝票も「未確定」として拾う
  AND fp.status = 'closed'

UNION ALL
SELECT
    '締め済み年度の下書き',
    je.id, je.entry_date, fy.name, NULL, je.description, je.source_type
FROM journal_entries je
JOIN fiscal_years fy ON fy.id = je.fiscal_year_id
WHERE COALESCE(je.status, '') <> 'posted'   -- status が NULL の伝票も「未確定」として拾う
  AND fy.status = 'closed'
