-- 計上した人を記録する（qa/02 R2-05）。正典は ddl/005_journals.sql の posted_by。
-- NULL 可: この列より前に計上された伝票と下書きは NULL のまま。
ALTER TABLE journal_entries ADD COLUMN posted_by INTEGER;
