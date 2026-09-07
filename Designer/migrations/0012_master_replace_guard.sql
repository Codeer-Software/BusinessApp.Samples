-- 使用中のマスタの REPLACE の経路を塞ぐ（ADR-0038。qa/03 L-26 と同じ型）。
--
-- 0011 のトリガは UPDATE にしか張っておらず、id を指定した INSERT OR REPLACE と
-- UPDATE OR REPLACE ... SET id で、計上済みの明細が参照している id を別の行が乗っ取れた
-- （2026-09-07 の自己レビューで実測。qa/02 のラウンド 45）。
-- **id を指定しない INSERT（seed・画面）には当たらない。**
--
-- 正典（ddl/005_journals.sql）からの逐語の写し。

CREATE TRIGGER trg_accounts_no_replace_used_insert
BEFORE INSERT ON accounts
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_accounts_no_replace_used_update
BEFORE UPDATE OF id ON accounts
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;

CREATE TRIGGER trg_sub_accounts_no_replace_used_insert
BEFORE INSERT ON sub_accounts
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている補助科目を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.sub_account_id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_sub_accounts_no_replace_used_update
BEFORE UPDATE OF id ON sub_accounts
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている補助科目を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.sub_account_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;

CREATE TRIGGER trg_departments_no_replace_used_insert
BEFORE INSERT ON departments
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている部門を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.department_id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_departments_no_replace_used_update
BEFORE UPDATE OF id ON departments
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている部門を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.department_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;

CREATE TRIGGER trg_tax_categories_no_replace_used_insert
BEFORE INSERT ON tax_categories
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている税区分を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.tax_category_id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_tax_categories_no_replace_used_update
BEFORE UPDATE OF id ON tax_categories
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている税区分を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.tax_category_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;
