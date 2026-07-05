-- 155_carryover_fix.sql — 翌期繰越の再実行不能バグの修正（総合テストで実測）
-- 原因: NextYearId が非バインドフィールドのため、他に変更差分が無い Submit では
--       UPDATE 文が発行されず、Update タイミングの ExecuteSqlField（CarryOverSql）が発火しない。
-- 対策: fiscal_years に next_year_id 列を追加して NextYearId をバインド化。
--       ボタン実行時に NULL→値 の実差分が生じ、確実に UPDATE + 繰越 SQL が走る
--       （実行後は NULL に書き戻すため、通常の保存で繰越が誤発火することはない）。
ALTER TABLE fiscal_years ADD COLUMN next_year_id INTEGER REFERENCES fiscal_years(id);
