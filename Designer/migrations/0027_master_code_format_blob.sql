-- 0027 コードの書式のトリガに、BLOB を弾く条件を足す
--
-- 正典は Designer/ddl/008_master_code_format.sql。
--
-- **BLOB は TEXT の列にそのまま入る**（2026-09-09 に実測。sqlite 3.53.1）。
--   insert into accounts (code, ...) values (x'4142', ...)   -- 通っていた
--   select typeof(code), hex(code) -> ('blob', '4142')
-- **GLOB は BLOB に「使える字」と答え**、0026 で足したバイト数と文字数の突き合わせも
-- **BLOB では両方がバイト数になって必ず等しくなる**ので、条件ごと死んでいた。
--
-- **`x'4142'` と `'AB'` は別の値**なので COLLATE NOCASE の一意索引でもぶつからず、
-- **見た目が同じ 2 行が並ぶ**——qa/03 L-32 を別の入口から開け直すことになる。
-- 関門（C# の MasterCode）は文字列しか作れないので、通るのは取込・CLI・SQL の直打ちである。
--
-- 数値は TEXT の列で text へ変換されるため、断るのは BLOB だけでよい。
-- NULL は列の NOT NULL が断る（「入れてください」の側の文言になる）。
--
-- **009 の 3 本も作り直す**（0026 と同じ理由——表ごとのトリガの作られた順を正典と合わせる）。
--
-- **既存の行は評価しない**（トリガは新しく書き込まれる値しか見ない。migrations/README）。

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
    SELECT RAISE(ABORT, '会計年度のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '会計年度のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '税区分のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '税区分のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '勘定科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '勘定科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '補助科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '補助科目のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '部門のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '部門のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '取引先のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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
    SELECT RAISE(ABORT, '取引先のコードは半角の英数字と「-」「_」で、20 文字以内です。「-」「_」は先頭と末尾には置けません。');
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

-- REPLACE で id を乗っ取る経路（qa/03 L-26 と同じ型）。
-- 使用中の id へ別の行を流し込むと、UPDATE のトリガを通らずに意味が変わる。
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
