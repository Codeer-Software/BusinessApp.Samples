-- 0029 RAISE の文言を画面の呼び名と関門に揃える（docs/04 §1 の B-1 の残り）
--
-- 正典は Designer/ddl/005_journals.sql（意味の凍結 4 本・取引先を要する 1 本）と
-- Designer/ddl/008_master_code_format.sql（コードの書式 12 本）。**規則（WHEN 節）は 1 字も変えない**——変えるのは RAISE の文言だけ。
--
--   意味の凍結    列ラベルの写し（「（補助科目コード・勘定科目）」）を落とす。「新しい科目」→「新しい勘定科目」
--   取引先を要する 「取引先を必須から外す」→「「取引先を要する」をオフにできない」（関門と同じ言い方）
--   コードの書式   「会計年度のコード」→「年度コード」ほか 6 表を画面のラベルに。4 つの条件を 1 本で断っていたのを条件ごとに分ける
--
-- **変えたトリガより後に作られた同じ表のトリガも、いったん落として正典の順で作り直す**——
-- トリガは CREATE OR REPLACE が無く、作り直すと末尾に付く。表ごとの作られた順は発火順に効く（仕様上 undefined。
-- MigrationEquivalenceTests が正典と照合する）ので、変えていないトリガも同じ文面で作り直している。

DROP TRIGGER trg_partners_code_format_insert;
DROP TRIGGER trg_partners_code_format_update;
DROP TRIGGER trg_partners_meaning_frozen_when_posted;
DROP TRIGGER trg_partners_no_replace_used_insert;
DROP TRIGGER trg_partners_no_replace_used_update;
DROP TRIGGER trg_partners_corporate_number_text_insert;
DROP TRIGGER trg_partners_corporate_number_text_update;
DROP TRIGGER trg_accounts_meaning_frozen_when_posted;
DROP TRIGGER trg_accounts_no_replace_used_insert;
DROP TRIGGER trg_accounts_no_replace_used_update;
DROP TRIGGER trg_accounts_requires_partner_not_loosened_when_posted;
DROP TRIGGER trg_accounts_code_format_insert;
DROP TRIGGER trg_accounts_code_format_update;
DROP TRIGGER trg_sub_accounts_meaning_frozen_when_posted;
DROP TRIGGER trg_sub_accounts_no_replace_used_insert;
DROP TRIGGER trg_sub_accounts_no_replace_used_update;
DROP TRIGGER trg_sub_accounts_code_format_insert;
DROP TRIGGER trg_sub_accounts_code_format_update;
DROP TRIGGER trg_departments_meaning_frozen_when_posted;
DROP TRIGGER trg_departments_no_replace_used_insert;
DROP TRIGGER trg_departments_no_replace_used_update;
DROP TRIGGER trg_departments_code_format_insert;
DROP TRIGGER trg_departments_code_format_update;
DROP TRIGGER trg_tax_categories_meaning_frozen_when_posted;
DROP TRIGGER trg_tax_categories_no_replace_used_insert;
DROP TRIGGER trg_tax_categories_no_replace_used_update;
DROP TRIGGER trg_tax_categories_code_format_insert;
DROP TRIGGER trg_tax_categories_code_format_update;
DROP TRIGGER trg_fiscal_years_code_format_insert;
DROP TRIGGER trg_fiscal_years_code_format_update;

CREATE TRIGGER trg_partners_code_format_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「取引先コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「取引先コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「取引先コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「取引先コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「取引先コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「取引先コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_partners_code_format_update
BEFORE UPDATE OF code ON partners
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「取引先コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「取引先コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「取引先コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「取引先コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「取引先コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「取引先コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_partners_meaning_frozen_when_posted
BEFORE UPDATE OF code ON partners
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先のコードは変更できない。新しい取引先を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id = OLD.id AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id = OLD.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_partners_no_replace_used_insert
BEFORE INSERT ON partners
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先は置き換えられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id = NEW.id AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_partners_no_replace_used_update
BEFORE UPDATE OF id ON partners
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳が使っている取引先は置き換えられない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted')
        OR EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.partner_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;

CREATE TRIGGER trg_partners_corporate_number_text_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN typeof(NEW.corporate_number) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '法人番号は 13 桁の数字で入れる。');
END;

CREATE TRIGGER trg_partners_corporate_number_text_update
BEFORE UPDATE OF corporate_number ON partners
FOR EACH ROW
WHEN typeof(NEW.corporate_number) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '法人番号は 13 桁の数字で入れる。');
END;

CREATE TRIGGER trg_accounts_meaning_frozen_when_posted
BEFORE UPDATE OF code, category, is_contra, uses_sub_account ON accounts
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.category IS NOT OLD.category
              OR NEW.is_contra IS NOT OLD.is_contra
              OR NEW.uses_sub_account IS NOT OLD.uses_sub_account
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目の意味は変更できない。新しい勘定科目を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id = OLD.id AND e.status = 'posted');
END;

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

CREATE TRIGGER trg_accounts_requires_partner_not_loosened_when_posted
BEFORE UPDATE OF requires_partner ON accounts
FOR EACH ROW WHEN OLD.requires_partner = 1 AND NEW.requires_partner = 0
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている勘定科目では、「取引先を要する」をオフにできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.account_id = OLD.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_accounts_code_format_insert
BEFORE INSERT ON accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「科目コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「科目コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「科目コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「科目コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「科目コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「科目コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_accounts_code_format_update
BEFORE UPDATE OF code ON accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「科目コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「科目コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「科目コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「科目コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「科目コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「科目コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_sub_accounts_meaning_frozen_when_posted
BEFORE UPDATE OF code, account_id ON sub_accounts
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.account_id IS NOT OLD.account_id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている補助科目の意味は変更できない。新しい補助科目を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.sub_account_id = OLD.id AND e.status = 'posted');
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

CREATE TRIGGER trg_sub_accounts_code_format_insert
BEFORE INSERT ON sub_accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「補助科目コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「補助科目コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「補助科目コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「補助科目コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_sub_accounts_code_format_update
BEFORE UPDATE OF code ON sub_accounts
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「補助科目コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「補助科目コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「補助科目コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「補助科目コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_departments_meaning_frozen_when_posted
BEFORE UPDATE OF code, is_company_wide ON departments
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.is_company_wide IS NOT OLD.is_company_wide
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている部門の意味は変更できない。新しい部門を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.department_id = OLD.id AND e.status = 'posted');
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

CREATE TRIGGER trg_departments_code_format_insert
BEFORE INSERT ON departments
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「部門コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「部門コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「部門コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「部門コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「部門コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「部門コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_departments_code_format_update
BEFORE UPDATE OF code ON departments
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「部門コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「部門コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「部門コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「部門コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「部門コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「部門コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_tax_categories_meaning_frozen_when_posted
BEFORE UPDATE OF code, taxation_type, rate_kind ON tax_categories
FOR EACH ROW WHEN NEW.code IS NOT OLD.code
              OR NEW.taxation_type IS NOT OLD.taxation_type
              OR NEW.rate_kind IS NOT OLD.rate_kind
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている税区分の意味は変更できない。新しい税区分を作る。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.tax_category_id = OLD.id AND e.status = 'posted');
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

CREATE TRIGGER trg_tax_categories_code_format_insert
BEFORE INSERT ON tax_categories
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「税区分コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「税区分コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「税区分コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「税区分コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「税区分コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「税区分コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_tax_categories_code_format_update
BEFORE UPDATE OF code ON tax_categories
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「税区分コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「税区分コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「税区分コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「税区分コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「税区分コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「税区分コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_fiscal_years_code_format_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「年度コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「年度コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「年度コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「年度コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「年度コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「年度コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;

CREATE TRIGGER trg_fiscal_years_code_format_update
BEFORE UPDATE OF code ON fiscal_years
FOR EACH ROW
WHEN NEW.code GLOB '*[^0-9A-Za-z_-]*'
     OR NEW.code GLOB '[-_]*'
     OR NEW.code GLOB '*[-_]'
     OR NEW.code GLOB '*[-_][-_]*'
     OR LENGTH(NEW.code) < 1
     OR LENGTH(NEW.code) > 20
     OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code)
     OR typeof(NEW.code) = 'blob'
BEGIN
    SELECT RAISE(ABORT, '「年度コード」に使えない字が入っています。半角の英数字と「-」「_」だけで入れてください。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「年度コード」の先頭に「-」「_」は置けません。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「年度コード」の末尾に「-」「_」は置けません。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「年度コード」の「-」「_」は続けて使えません。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「年度コード」は 20 文字以内です。')
     WHERE LENGTH(NEW.code) > 20;
    SELECT RAISE(ABORT, '「年度コード」を入れてください。')
     WHERE LENGTH(NEW.code) < 1;
END;
