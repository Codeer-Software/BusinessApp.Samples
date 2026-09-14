--------------------------------------------------------------------------------
-- 0037 伝票の摘要と明細の内容の上限（docs/10 §4-2-1）
--
-- **既にある行は見ない**（トリガは追加と更新のときだけ鳴る）ので、適用は必ず成功する。
-- **長すぎる行が残っていると、その伝票は取り消せなくなる**——
-- 取消は原仕訳の摘要を写して新しい伝票を作るので、写した先でこのトリガに当たる。
-- **適用前に必ず数えること。**
--
--   SELECT 'journal_entries.description' AS 欄, COUNT(*) FROM journal_entries
--    WHERE description IS NOT NULL
--      AND (typeof(description) = 'blob'
--        OR instr(CAST(description AS BLOB), x'00') > 0
--        OR LENGTH(CAST(description AS BLOB)) > 4 * LENGTH(description)
--        OR LENGTH(description) > 200)
--   UNION ALL SELECT 'journal_lines.item_description', COUNT(*) FROM journal_lines
--    WHERE item_description IS NOT NULL
--      AND (typeof(item_description) = 'blob'
--        OR instr(CAST(item_description AS BLOB), x'00') > 0
--        OR LENGTH(CAST(item_description AS BLOB)) > 4 * LENGTH(item_description)
--        OR LENGTH(item_description) > 200);
--
-- **2026-09-14 に稼働 DB で 2 欄とも 0 件。**
--------------------------------------------------------------------------------

CREATE TRIGGER trg_journal_entries_description_length_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW
WHEN NEW.description IS NOT NULL
     AND (LENGTH(NEW.description) > 200
          OR typeof(NEW.description) = 'blob'
          OR instr(CAST(NEW.description AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.description AS BLOB)) > 4 * LENGTH(NEW.description))
BEGIN
    SELECT RAISE(ABORT, '「摘要」に使えない字が入っている。')
     WHERE typeof(NEW.description) = 'blob'
        OR instr(CAST(NEW.description AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.description AS BLOB)) > 4 * LENGTH(NEW.description);
    SELECT RAISE(ABORT, '「摘要」は 200 文字以内。')
     WHERE LENGTH(NEW.description) > 200;
END;

CREATE TRIGGER trg_journal_entries_description_length_update
BEFORE UPDATE OF description ON journal_entries
FOR EACH ROW
WHEN NEW.description IS NOT NULL
     AND (LENGTH(NEW.description) > 200
          OR typeof(NEW.description) = 'blob'
          OR instr(CAST(NEW.description AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.description AS BLOB)) > 4 * LENGTH(NEW.description))
BEGIN
    SELECT RAISE(ABORT, '「摘要」に使えない字が入っている。')
     WHERE typeof(NEW.description) = 'blob'
        OR instr(CAST(NEW.description AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.description AS BLOB)) > 4 * LENGTH(NEW.description);
    SELECT RAISE(ABORT, '「摘要」は 200 文字以内。')
     WHERE LENGTH(NEW.description) > 200;
END;

CREATE TRIGGER trg_journal_lines_item_description_length_insert
BEFORE INSERT ON journal_lines
FOR EACH ROW
WHEN NEW.item_description IS NOT NULL
     AND (LENGTH(NEW.item_description) > 200
          OR typeof(NEW.item_description) = 'blob'
          OR instr(CAST(NEW.item_description AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.item_description AS BLOB)) > 4 * LENGTH(NEW.item_description))
BEGIN
    SELECT RAISE(ABORT, '「内容」に使えない字が入っている。')
     WHERE typeof(NEW.item_description) = 'blob'
        OR instr(CAST(NEW.item_description AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.item_description AS BLOB)) > 4 * LENGTH(NEW.item_description);
    SELECT RAISE(ABORT, '「内容」は 200 文字以内。')
     WHERE LENGTH(NEW.item_description) > 200;
END;

CREATE TRIGGER trg_journal_lines_item_description_length_update
BEFORE UPDATE OF item_description ON journal_lines
FOR EACH ROW
WHEN NEW.item_description IS NOT NULL
     AND (LENGTH(NEW.item_description) > 200
          OR typeof(NEW.item_description) = 'blob'
          OR instr(CAST(NEW.item_description AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.item_description AS BLOB)) > 4 * LENGTH(NEW.item_description))
BEGIN
    SELECT RAISE(ABORT, '「内容」に使えない字が入っている。')
     WHERE typeof(NEW.item_description) = 'blob'
        OR instr(CAST(NEW.item_description AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.item_description AS BLOB)) > 4 * LENGTH(NEW.item_description);
    SELECT RAISE(ABORT, '「内容」は 200 文字以内。')
     WHERE LENGTH(NEW.item_description) > 200;
END;
