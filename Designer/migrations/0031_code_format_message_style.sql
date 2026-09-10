-- 0031 コードの書式の断りを「だ・である」に揃え、空の条件を先頭にする
--
-- 正典は Designer/ddl/008_master_code_format.sql（12 本）。文言の規則は docs/12 §2-1。WHEN 節は変えていない。
-- 0029 の直後の直し（自己レビュー ラウンド 72）。変えた 12 本と、同じ表でそれより後に作られた 5 本（取引先）を、
-- 正典の順で落として作り直す。

DROP TRIGGER trg_fiscal_years_code_format_insert;
DROP TRIGGER trg_fiscal_years_code_format_update;
DROP TRIGGER trg_tax_categories_code_format_insert;
DROP TRIGGER trg_tax_categories_code_format_update;
DROP TRIGGER trg_accounts_code_format_insert;
DROP TRIGGER trg_accounts_code_format_update;
DROP TRIGGER trg_sub_accounts_code_format_insert;
DROP TRIGGER trg_sub_accounts_code_format_update;
DROP TRIGGER trg_departments_code_format_insert;
DROP TRIGGER trg_departments_code_format_update;
DROP TRIGGER trg_partners_code_format_insert;
DROP TRIGGER trg_partners_code_format_update;
DROP TRIGGER trg_partners_meaning_frozen_when_posted;
DROP TRIGGER trg_partners_no_replace_used_insert;
DROP TRIGGER trg_partners_no_replace_used_update;
DROP TRIGGER trg_partners_corporate_number_text_insert;
DROP TRIGGER trg_partners_corporate_number_text_update;

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
    SELECT RAISE(ABORT, '「年度コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「年度コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「年度コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「年度コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「年度コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「年度コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「年度コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「年度コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「年度コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「年度コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「年度コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「年度コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「税区分コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「税区分コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「税区分コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「税区分コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「税区分コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「税区分コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「税区分コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「税区分コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「税区分コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「税区分コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「税区分コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「税区分コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「科目コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「科目コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「科目コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「科目コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「科目コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「科目コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「科目コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「科目コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「科目コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「科目コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「科目コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「科目コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「補助科目コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「補助科目コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「補助科目コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「補助科目コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「補助科目コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「補助科目コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「補助科目コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「補助科目コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「補助科目コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「部門コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「部門コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「部門コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「部門コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「部門コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「部門コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「部門コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「部門コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「部門コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「部門コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「部門コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「部門コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
END;

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
    SELECT RAISE(ABORT, '「取引先コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「取引先コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「取引先コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「取引先コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「取引先コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「取引先コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
    SELECT RAISE(ABORT, '「取引先コード」が空である。')
     WHERE typeof(NEW.code) <> 'blob' AND LENGTH(NEW.code) < 1;
    SELECT RAISE(ABORT, '「取引先コード」に使えない字が入っている。半角の英数字と「-」「_」だけを使う。')
     WHERE typeof(NEW.code) = 'blob'
        OR NEW.code GLOB '*[^0-9A-Za-z_-]*'
        OR LENGTH(CAST(NEW.code AS BLOB)) <> LENGTH(NEW.code);
    SELECT RAISE(ABORT, '「取引先コード」の先頭に「-」「_」は置けない。')
     WHERE NEW.code GLOB '[-_]*';
    SELECT RAISE(ABORT, '「取引先コード」の末尾に「-」「_」は置けない。')
     WHERE NEW.code GLOB '*[-_]';
    SELECT RAISE(ABORT, '「取引先コード」の「-」「_」は続けて使えない。')
     WHERE NEW.code GLOB '*[-_][-_]*';
    SELECT RAISE(ABORT, '「取引先コード」は 20 文字以内。')
     WHERE LENGTH(NEW.code) > 20;
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
