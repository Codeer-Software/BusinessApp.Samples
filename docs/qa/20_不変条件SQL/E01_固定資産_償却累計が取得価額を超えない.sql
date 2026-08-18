-- 何を保証するか: 固定資産ごとの減価償却累計額が取得価額を超えないこと（過大償却の検出）。
--                 本アプリは直接法（借方 減価償却費 6300 / 貸方 資産科目）なので、
--                 累計 = その資産に紐づく償却仕訳の貸方合計。
-- 違反時の意味: 資産の簿価がマイナスになる。BS の固定資産が実在しない負の値を持つ。
--               償却仕訳の二重生成、または耐用年数変更後の再計算漏れ。
-- 出典: docs/04_会計ドメイン設計.md §7（直接法・残存簿価 1 円まで）
--       Modules/Accounting/FixedAsset.mod.cs（source_type='depreciation', source_id=資産id で起票）
-- 備考: 「残存簿価 1 円」まで償却する規約なので、累計 = 取得価額 - 1 は正常。
--       累計 > 取得価額 - 1（＝簿価 0 以下）になった行を返す。
-- 数え方は ADR-0073 に合わせる（2026-08-18）:
--   ① 自動生成した償却仕訳（source_type='depreciation'）
--   ② **伝票ヘッダの「固定資産」欄でこの資産を指した伝票**＝手で打った償却の訂正
--   処分仕訳（disposal）は簿価を落とす仕訳であって償却ではないので数えない。
--   金額は**その資産の計上科目に立った行の（貸方 − 借方）**で測る（直接法）。
--   アプリの画面と同じ式にしておかないと、訂正で過大償却になっても検査が素通りする。
WITH dep AS (
  SELECT fa.id AS asset_id,
         SUM(CASE WHEN jl.dc = 'C' THEN jl.amount ELSE -jl.amount END) AS accum
  FROM fixed_assets fa
  JOIN journal_entries je
    ON je.status = 'posted'
   AND COALESCE(je.source_type, '') <> 'disposal'
   AND ((je.source_type = 'depreciation' AND je.source_id = fa.id)
        OR je.fixed_asset_id = fa.id)
  JOIN journal_lines jl ON jl.journal_entry_id = je.id
                       AND jl.account_id = fa.asset_account_id
  GROUP BY fa.id
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
