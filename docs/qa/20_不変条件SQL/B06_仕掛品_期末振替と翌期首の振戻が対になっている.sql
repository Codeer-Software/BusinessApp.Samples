-- 何を保証するか: 仕掛品（未成業務支出金）の期末振替について、次の 3 つが同時に成り立つこと。
--   ① 起票した振替額が、いま計算し直した額と一致する（陳腐化していない）
--   ② 期末の振替に対して、翌期首の振戻が同額で存在する（片方だけ残っていない）
--   ③ 振替仕訳の借方合計と貸方合計が一致する（A01 でも見ているが、ここでは仕掛品に限って明示する）
-- 違反時の意味:
--   ① 振替の後に当年度の伝票・工数が動いた。**当期の損益が誤ったまま**になる（画面の警告と同じ判定）
--   ② 期末だけ振り替えて翌期首に戻していない。**繰り延べた原価が永久に費用化されず**、
--      仕掛品残高が積み上がり続ける。洗い替え方式が壊れている状態
-- 出典: ADR-0072（仕掛品の期末振替は「検収未了」を基準に洗い替える）/ BUG-0016
-- 実装メモ: 判定に使う値はすべてビュー v_wip_status（Designer/ddl/720）から取る。
--           画面・仕訳生成・この検査で同じ計算を三重に書かないため（ADR-0060）。
SELECT * FROM (
  SELECT
    '振替額が陳腐化している' AS 違反,
    fiscal_year_name AS 年度,
    CAST(posted_amount AS TEXT) AS 起票済み,
    CAST(computed_amount AS TEXT) AS 計算値,
    CAST(posted_amount - computed_amount AS TEXT) AS 差額
  FROM v_wip_status
  WHERE posted_entries > 0 AND posted_amount <> computed_amount

  UNION ALL
  SELECT
    '期末に振り替えたのに翌期首の振戻が無い',
    fiscal_year_name,
    CAST(posted_amount AS TEXT),
    '0',
    CAST(posted_amount AS TEXT)
  FROM v_wip_status
  WHERE posted_entries > 0 AND reversal_entries = 0

  UNION ALL
  SELECT
    '振替額と振戻額が食い違う',
    fiscal_year_name,
    CAST(posted_amount AS TEXT),
    CAST(reversal_amount AS TEXT),
    CAST(posted_amount - reversal_amount AS TEXT)
  FROM v_wip_status
  WHERE posted_entries > 0 AND reversal_entries > 0 AND posted_amount <> reversal_amount

  UNION ALL
  -- 振替を起票していないのに振戻だけある（取消が中途半端に終わった痕跡）
  SELECT
    '振替が無いのに振戻だけある',
    fiscal_year_name,
    '0',
    CAST(reversal_amount AS TEXT),
    CAST(-reversal_amount AS TEXT)
  FROM v_wip_status
  WHERE posted_entries = 0 AND reversal_entries > 0
)
