-- 伝票ヘッダの取引先名の写しを足し、明細を計上済みの伝票へ付け替える道を塞ぐ。
--
-- **1 本にまとめる理由**: どちらも journal_entries / journal_lines の同じ回の変更で、
-- どちらも「計上済みの伝票が計上時の姿から動かない」ことを守るものだから
-- （ADR-0037 と ADR-0004）。
--
-- **既存の計上済み伝票は埋め直さない。** 計上時点の名前が分からない以上、
-- いまのマスタ名で埋めると「計上時の写し」という嘘になる。NULL のままにし、
-- 画面はそのときだけ現在のマスタ名へ落ちる（帳簿の COALESCE と同じ考え方）。

ALTER TABLE journal_entries ADD COLUMN partner_name_snapshot TEXT;

-- 正典（ddl/005_journals.sql）からの逐語の写し。
CREATE TRIGGER trg_journal_lines_no_move_into_posted
BEFORE UPDATE ON journal_lines
FOR EACH ROW WHEN (SELECT status FROM journal_entries WHERE id = NEW.journal_entry_id) = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳へ明細を移動できない。');
END;

-- REPLACE の暗黙の DELETE を塞ぐ（正典からの逐語の写し）。
CREATE TRIGGER trg_journal_entries_no_replace_posted_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳を上書きできない。訂正・取消は反対仕訳で行う。')
     WHERE EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.status = 'posted'
                      AND (e.id = NEW.id
                           OR (NEW.idempotency_key IS NOT NULL
                               AND e.idempotency_key = NEW.idempotency_key)));
END;

CREATE TRIGGER trg_journal_entries_no_replace_posted_update
BEFORE UPDATE ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳を上書きできない。訂正・取消は反対仕訳で行う。')
     WHERE EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.status = 'posted' AND e.id <> OLD.id
                      AND (e.id = NEW.id
                           OR (NEW.idempotency_key IS NOT NULL
                               AND e.idempotency_key = NEW.idempotency_key)));
END;

CREATE TRIGGER trg_journal_lines_no_replace_posted_insert
BEFORE INSERT ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_journal_lines_no_replace_posted_update
BEFORE UPDATE ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.id = NEW.id AND l.id <> OLD.id AND e.status = 'posted');
END;
