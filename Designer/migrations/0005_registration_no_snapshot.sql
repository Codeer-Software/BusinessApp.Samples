-- 計上時点の登録番号の写しを明細に持つ（docs/13 §4・ADR-0018）。
-- 正典は ddl/005_journals.sql の registration_no_snapshot。
-- NULL 可: この列より前に計上された明細と、登録が引けなかった明細は NULL のまま。
-- 計上済みは不変（ADR-0004）なので、既存の行は遡って埋めない。
ALTER TABLE journal_lines ADD COLUMN registration_no_snapshot TEXT;
