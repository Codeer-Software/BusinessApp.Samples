-- 何を保証するか: 伝票の entry_date が、その伝票が持つ fiscal_year_id の期間内に収まっていること。
-- 違反時の意味: 年度跨ぎ。fiscal_year_id を条件にする帳票（試算表の期首・繰越）と
--               entry_date を条件にする帳票（元帳・PL）で数字が食い違う。
--               どちらの経路で集計するかは画面ごとに違うため、ズレると原因追跡が非常に困難になる。
-- 出典: docs/04_会計ドメイン設計.md §3（fiscal_year_id は entry_date から導出して保持・年度跨ぎ防止）
SELECT
    je.id             AS 伝票id,
    je.journal_no     AS 伝票番号,
    je.entry_date     AS 日付,
    fy.name           AS 保持年度,
    fy.start_date     AS 年度開始,
    fy.end_date       AS 年度終了,
    je.status         AS 状態,
    je.source_type    AS 連動元
FROM journal_entries je
JOIN fiscal_years fy ON fy.id = je.fiscal_year_id
WHERE date(je.entry_date) < date(fy.start_date)
   OR date(je.entry_date) > date(fy.end_date)
ORDER BY je.entry_date, je.id
