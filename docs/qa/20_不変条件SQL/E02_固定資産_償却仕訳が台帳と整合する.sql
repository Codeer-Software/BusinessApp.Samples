-- 何を保証するか: 減価償却仕訳が固定資産台帳と整合していること。
--   (a) source_id が実在する固定資産を指している（孤児償却仕訳が無い）
--   (b) 貸方の資産科目が台帳の計上科目（asset_account_id）と一致する
--   (c) 同一資産・同一年度の償却仕訳が 2 本以上ない（二重生成ガードのすり抜け）
-- 違反時の意味: 償却費がどの資産のものか追えない／別の資産科目を減らしている。
--               固定資産台帳と BS の固定資産が突合できなくなる。
-- 出典: Modules/Accounting/FixedAsset.mod.cs（二重生成ガード・借方 6300 / 貸方 資産科目）
--       docs/04_会計ドメイン設計.md §7
SELECT '対象資産が実在しない' AS 違反, je.id AS 伝票id, je.entry_date AS 日付,
       je.source_id AS 資産id, NULL AS 台帳科目, NULL AS 仕訳科目, NULL AS 件数
FROM journal_entries je
WHERE je.source_type = 'depreciation'
  AND NOT EXISTS (SELECT 1 FROM fixed_assets fa WHERE fa.id = je.source_id)

UNION ALL
SELECT '貸方科目が台帳の計上科目と違う', je.id, je.entry_date, fa.id,
       la.code || ' ' || la.name, ja.code || ' ' || ja.name, NULL
FROM journal_entries je
JOIN fixed_assets fa ON fa.id = je.source_id
JOIN journal_lines jl ON jl.journal_entry_id = je.id AND jl.dc = 'C'
JOIN accounts ja ON ja.id = jl.account_id
JOIN accounts la ON la.id = fa.asset_account_id
WHERE je.source_type = 'depreciation'
  AND jl.account_id <> fa.asset_account_id

UNION ALL
SELECT '同一資産・同一年度に償却仕訳が複数', NULL, NULL, je.source_id, NULL, NULL, COUNT(*)
FROM journal_entries je
WHERE je.source_type = 'depreciation'
GROUP BY je.source_id, je.fiscal_year_id
HAVING COUNT(*) > 1
