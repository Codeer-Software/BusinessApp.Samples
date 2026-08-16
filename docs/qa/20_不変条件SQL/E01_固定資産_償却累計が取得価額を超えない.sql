-- 何を保証するか: 固定資産ごとの減価償却累計額が取得価額を超えないこと（過大償却の検出）。
--                 本アプリは直接法（借方 減価償却費 6300 / 貸方 資産科目）なので、
--                 累計 = その資産に紐づく償却仕訳の貸方合計。
-- 違反時の意味: 資産の簿価がマイナスになる。BS の固定資産が実在しない負の値を持つ。
--               償却仕訳の二重生成、または耐用年数変更後の再計算漏れ。
-- 出典: docs/04_会計ドメイン設計.md §7（直接法・残存簿価 1 円まで）
--       Modules/Accounting/FixedAsset.mod.cs（source_type='depreciation', source_id=資産id で起票）
-- 備考: 「残存簿価 1 円」まで償却する規約なので、累計 = 取得価額 - 1 は正常。
--       累計 > 取得価額 - 1（＝簿価 0 以下）になった行を返す。
WITH dep AS (
  SELECT je.source_id AS asset_id,
         SUM(CASE WHEN jl.dc = 'C' THEN jl.amount ELSE -jl.amount END) AS accum
  FROM journal_entries je
  JOIN journal_lines jl ON jl.journal_entry_id = je.id
  JOIN accounts a ON a.id = jl.account_id
  WHERE je.source_type = 'depreciation'
    AND je.status = 'posted'
    AND a.account_type = 'asset'
  GROUP BY je.source_id
)
SELECT
    fa.id   AS 資産id,
    fa.code AS 資産コード,
    fa.name AS 資産名,
    fa.depreciation_method AS 償却方法,
    fa.useful_life         AS 耐用年数,
    fa.status              AS 状態,
    fa.acquisition_cost    AS 取得価額,
    d.accum                AS 償却累計,
    fa.acquisition_cost - d.accum AS 簿価
FROM fixed_assets fa
JOIN dep d ON d.asset_id = fa.id
WHERE d.accum > fa.acquisition_cost - 1
ORDER BY fa.code
