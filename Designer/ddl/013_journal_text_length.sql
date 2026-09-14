--------------------------------------------------------------------------------
-- 伝票の摘要と明細の内容の上限（docs/10 §4-2-1。2026-09-13 の開発者の決定）
--
-- **012 と同じ規則を、伝票の 2 列へ広げたものである**（条件の骨格はまったく同じで、
-- `FieldLengthConsistencyTests` が 20 本を突き合わせる）。**別のファイルにしてあるのは、
-- 012 が「マスタと取引先」、こちらが「伝票」で、決めた文書が違うから**である
-- （docs/12 §2-2 と docs/10 §4-2-1）。
--
-- **数えられない値は 3 通りとも断る**（BLOB・NUL を含む TEXT・壊れた UTF-8）。
-- 理由は 012 の見出しに書いた（NUL をバイト数の比で探すと穴が開く。docs/qa/03 の L-48）。
--
-- **取消・訂正の摘要はサーバが組み立てる**（`AmendmentRules.Describe`）。
-- 前置き（「伝票番号 N の取消: 」）があるので、**原仕訳の摘要が上限いっぱいだとそのままでは超える**——
-- **本文の側を詰めて収める**ようにしてある。**ここで拒むと、利用者には直す手立てが無い**
-- （取消の摘要は画面から書けず、計上済みは変えられない）。
--
-- **摘要が空かどうかはこのファイルの仕事ではない。** 計上のときだけ必須で、
-- それは 005 の trg_journal_entries_description_required_when_posted が見る。
--
-- **トリガ・インデックスは、このファイルの末尾に足す**（migrations/README の規約）。
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
