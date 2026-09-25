-- 明細に同時操作の版（楽観ロック）を持たせる（docs/decisions/0070）。
-- 正典は ddl/005_journals.sql の journal_lines.optimistic_locking。
-- 既存の行は 0 から始まる（伝票の同じ列と同じ）。進めるのは CLB の保存だけである。
ALTER TABLE journal_lines ADD COLUMN optimistic_locking INTEGER NOT NULL DEFAULT 0;
