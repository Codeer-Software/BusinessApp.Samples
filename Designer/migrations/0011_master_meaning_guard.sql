-- 使用中のマスタは意味を変えられない（ADR-0038。docs/04 §1 の A-1）。
--
-- 計上済みの仕訳明細が参照しているマスタの行について、意味を決める列の UPDATE を拒む
-- トリガ 4 本を足す。関門（MasterMeaningGate）が利用者の言葉で先に断り、
-- ここはアプリを迂回する経路への最後の守りになる。
-- **INSERT には張らない**ので、既存の行と seed の投入は止まらない。
--
-- 正典（ddl/005_journals.sql）からの逐語の写し。

CREATE TRIGGER trg_accounts_meaning_frozen_when_posted
BEFORE UPDATE OF code, category, is_contra, requires_sub_account ON accounts
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.category IS NOT OLD.category
              OR NEW.is_contra IS NOT OLD.is_contra
              OR NEW.requires_sub_account IS NOT OLD.requires_sub_account
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目の意味（科目コード・科目区分・評価勘定・補助科目を使う）は変更できない。新しい科目を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id = OLD.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_sub_accounts_meaning_frozen_when_posted
BEFORE UPDATE OF code, account_id ON sub_accounts
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.account_id IS NOT OLD.account_id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている補助科目の意味（補助科目コード・勘定科目）は変更できない。新しい補助科目を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.sub_account_id = OLD.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_departments_meaning_frozen_when_posted
BEFORE UPDATE OF code, is_company_wide ON departments
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.is_company_wide IS NOT OLD.is_company_wide
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている部門の意味（部門コード・全社共通）は変更できない。新しい部門を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.department_id = OLD.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_tax_categories_meaning_frozen_when_posted
BEFORE UPDATE OF code, taxation_type, rate_kind ON tax_categories
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.taxation_type IS NOT OLD.taxation_type
              OR NEW.rate_kind IS NOT OLD.rate_kind
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている税区分の意味（税区分コード・課税区分・税率区分）は変更できない。新しい税区分を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.tax_category_id = OLD.id AND e.status = 'posted');
END;
