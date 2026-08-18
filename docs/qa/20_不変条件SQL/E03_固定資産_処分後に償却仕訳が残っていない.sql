-- 何を保証するか: 除却・売却済みの固定資産に、処分日より後の減価償却仕訳が存在しないこと。
--   (a) 処分日（retired_date）より後の日付の償却仕訳が無い
--   (b) 処分済み（retired / sold）なら処分日が入っている（無いと (a) が検査できない＝静かに素通りする）
--   (c) 使用中（in_use）の資産に処分仕訳（source_type='disposal'）が残っていない
-- 違反時の意味: 手放した資産を償却し続けている。減価償却費が過大・BS の固定資産が実在しない値を持つ。
--               (c) は処分の取消が中途半端に終わった状態で、資産が二重に落ちている。
-- 出典: Modules/Accounting/FixedAsset.mod.cs（処分は処分日付で期中償却→処分仕訳。
--       償却生成は Status='in_use' のみ。取消は締め前に限り仕訳ごと削除）
--       docs/qa/02_バグ台帳.md BUG-0095
-- 備考: 処分時の期中償却は**処分日と同じ日付**で起票する。期末日付で起票すると
--       それだけでこの不変条件を破るので、日付の付け方の見張りも兼ねている。
SELECT '処分日より後の償却仕訳がある' AS 違反,
       fa.id AS 資産id, fa.code AS 資産コード, fa.name AS 資産名,
       fa.status AS 状態, fa.retired_date AS 処分日,
       je.journal_no AS 伝票番号, je.entry_date AS 仕訳日
FROM fixed_assets fa
JOIN journal_entries je
  ON COALESCE(je.source_type, '') <> 'disposal'
 AND ((je.source_type = 'depreciation' AND je.source_id = fa.id)
      OR je.fixed_asset_id = fa.id)   -- 手で打った訂正伝票も見る（ADR-0073）
WHERE fa.status IN ('retired', 'sold')
  AND fa.retired_date IS NOT NULL
  AND date(je.entry_date) > date(fa.retired_date)

UNION ALL
SELECT '処分済みなのに処分日が空',
       fa.id, fa.code, fa.name, fa.status, fa.retired_date, NULL, NULL
FROM fixed_assets fa
WHERE fa.status IN ('retired', 'sold')
  AND fa.retired_date IS NULL

UNION ALL
SELECT '使用中なのに処分仕訳が残っている',
       fa.id, fa.code, fa.name, fa.status, fa.retired_date,
       je.journal_no, je.entry_date
FROM fixed_assets fa
JOIN journal_entries je
  ON je.source_type = 'disposal'
 AND je.source_id = fa.id
WHERE fa.status = 'in_use'
ORDER BY 資産コード
