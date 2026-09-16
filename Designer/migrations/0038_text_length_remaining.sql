-- 0038 まだ上限の無かった文字の欄に上限を置き、郵便番号に書式を置く（docs/12 §2-2。旧 Q-26 の決定。2026-09-16）
--
-- **開発者の決定**（2026-09-16。逐語「A. 案どおり（推奨）」）。
-- 勘定科目・補助科目のカナ 60／取引先のカナ 100 → 200／自社情報の 5 欄／郵便番号の書式。
--
-- **現在形の正典は `Designer/ddl/012_text_length.sql`**（ADR-0020）。ここは差分だけを配る。
-- **既に入っている長い値は直さない**——更新のトリガは `BEFORE UPDATE OF <列>` なので、
-- その列に触らない限り通る（012 の見出し）。

DROP TRIGGER IF EXISTS trg_partners_name_kana_length_insert;
DROP TRIGGER IF EXISTS trg_partners_name_kana_length_update;

CREATE TRIGGER trg_partners_name_kana_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 200
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「取引先のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「取引先のカナ」は 200 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 200;
END;

CREATE TRIGGER trg_partners_name_kana_length_update
BEFORE UPDATE OF name_kana ON partners
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 200
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「取引先のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「取引先のカナ」は 200 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 200;
END;

CREATE TRIGGER trg_accounts_name_kana_length_insert
BEFORE INSERT ON accounts
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 60
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「勘定科目のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「勘定科目のカナ」は 60 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 60;
END;

CREATE TRIGGER trg_accounts_name_kana_length_update
BEFORE UPDATE OF name_kana ON accounts
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 60
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「勘定科目のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「勘定科目のカナ」は 60 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 60;
END;

CREATE TRIGGER trg_sub_accounts_name_kana_length_insert
BEFORE INSERT ON sub_accounts
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 60
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「補助科目のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「補助科目のカナ」は 60 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 60;
END;

CREATE TRIGGER trg_sub_accounts_name_kana_length_update
BEFORE UPDATE OF name_kana ON sub_accounts
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 60
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「補助科目のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「補助科目のカナ」は 60 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 60;
END;

CREATE TRIGGER trg_company_profile_name_length_insert
BEFORE INSERT ON company_profile
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 100
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「会社名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「会社名」は 100 文字以内。')
     WHERE LENGTH(NEW.name) > 100;
END;

CREATE TRIGGER trg_company_profile_name_length_update
BEFORE UPDATE OF name ON company_profile
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 100
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「会社名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「会社名」は 100 文字以内。')
     WHERE LENGTH(NEW.name) > 100;
END;

CREATE TRIGGER trg_company_profile_name_kana_length_insert
BEFORE INSERT ON company_profile
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 200
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「自社情報のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「自社情報のカナ」は 200 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 200;
END;

CREATE TRIGGER trg_company_profile_name_kana_length_update
BEFORE UPDATE OF name_kana ON company_profile
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 200
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「自社情報のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「自社情報のカナ」は 200 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 200;
END;

CREATE TRIGGER trg_company_profile_representative_name_length_insert
BEFORE INSERT ON company_profile
FOR EACH ROW
WHEN NEW.representative_name IS NOT NULL
     AND (LENGTH(NEW.representative_name) > 30
          OR typeof(NEW.representative_name) = 'blob'
          OR instr(CAST(NEW.representative_name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.representative_name AS BLOB)) > 4 * LENGTH(NEW.representative_name))
BEGIN
    SELECT RAISE(ABORT, '「代表者名」に使えない字が入っている。')
     WHERE typeof(NEW.representative_name) = 'blob'
        OR instr(CAST(NEW.representative_name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.representative_name AS BLOB)) > 4 * LENGTH(NEW.representative_name);
    SELECT RAISE(ABORT, '「代表者名」は 30 文字以内。')
     WHERE LENGTH(NEW.representative_name) > 30;
END;

CREATE TRIGGER trg_company_profile_representative_name_length_update
BEFORE UPDATE OF representative_name ON company_profile
FOR EACH ROW
WHEN NEW.representative_name IS NOT NULL
     AND (LENGTH(NEW.representative_name) > 30
          OR typeof(NEW.representative_name) = 'blob'
          OR instr(CAST(NEW.representative_name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.representative_name AS BLOB)) > 4 * LENGTH(NEW.representative_name))
BEGIN
    SELECT RAISE(ABORT, '「代表者名」に使えない字が入っている。')
     WHERE typeof(NEW.representative_name) = 'blob'
        OR instr(CAST(NEW.representative_name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.representative_name AS BLOB)) > 4 * LENGTH(NEW.representative_name);
    SELECT RAISE(ABORT, '「代表者名」は 30 文字以内。')
     WHERE LENGTH(NEW.representative_name) > 30;
END;

CREATE TRIGGER trg_company_profile_address_length_insert
BEFORE INSERT ON company_profile
FOR EACH ROW
WHEN NEW.address IS NOT NULL
     AND (LENGTH(NEW.address) > 200
          OR typeof(NEW.address) = 'blob'
          OR instr(CAST(NEW.address AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「住所」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR instr(CAST(NEW.address AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address);
    SELECT RAISE(ABORT, '「住所」は 200 文字以内。')
     WHERE LENGTH(NEW.address) > 200;
END;

CREATE TRIGGER trg_company_profile_address_length_update
BEFORE UPDATE OF address ON company_profile
FOR EACH ROW
WHEN NEW.address IS NOT NULL
     AND (LENGTH(NEW.address) > 200
          OR typeof(NEW.address) = 'blob'
          OR instr(CAST(NEW.address AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「住所」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR instr(CAST(NEW.address AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address);
    SELECT RAISE(ABORT, '「住所」は 200 文字以内。')
     WHERE LENGTH(NEW.address) > 200;
END;

CREATE TRIGGER trg_company_profile_phone_number_length_insert
BEFORE INSERT ON company_profile
FOR EACH ROW
WHEN NEW.phone_number IS NOT NULL
     AND (LENGTH(NEW.phone_number) > 20
          OR typeof(NEW.phone_number) = 'blob'
          OR instr(CAST(NEW.phone_number AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.phone_number AS BLOB)) > 4 * LENGTH(NEW.phone_number))
BEGIN
    SELECT RAISE(ABORT, '「電話番号」に使えない字が入っている。')
     WHERE typeof(NEW.phone_number) = 'blob'
        OR instr(CAST(NEW.phone_number AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.phone_number AS BLOB)) > 4 * LENGTH(NEW.phone_number);
    SELECT RAISE(ABORT, '「電話番号」は 20 文字以内。')
     WHERE LENGTH(NEW.phone_number) > 20;
END;

CREATE TRIGGER trg_company_profile_phone_number_length_update
BEFORE UPDATE OF phone_number ON company_profile
FOR EACH ROW
WHEN NEW.phone_number IS NOT NULL
     AND (LENGTH(NEW.phone_number) > 20
          OR typeof(NEW.phone_number) = 'blob'
          OR instr(CAST(NEW.phone_number AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.phone_number AS BLOB)) > 4 * LENGTH(NEW.phone_number))
BEGIN
    SELECT RAISE(ABORT, '「電話番号」に使えない字が入っている。')
     WHERE typeof(NEW.phone_number) = 'blob'
        OR instr(CAST(NEW.phone_number AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.phone_number AS BLOB)) > 4 * LENGTH(NEW.phone_number);
    SELECT RAISE(ABORT, '「電話番号」は 20 文字以内。')
     WHERE LENGTH(NEW.phone_number) > 20;
END;

CREATE TRIGGER trg_company_profile_postal_code_format_insert
BEFORE INSERT ON company_profile
FOR EACH ROW
WHEN NEW.postal_code IS NOT NULL
     AND (typeof(NEW.postal_code) = 'blob'
          OR instr(CAST(NEW.postal_code AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.postal_code AS BLOB)) > 4 * LENGTH(NEW.postal_code)
          OR NEW.postal_code NOT GLOB '[0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]')
BEGIN
    SELECT RAISE(ABORT, '「郵便番号」に使えない字が入っている。')
     WHERE typeof(NEW.postal_code) = 'blob'
        OR instr(CAST(NEW.postal_code AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.postal_code AS BLOB)) > 4 * LENGTH(NEW.postal_code);
    SELECT RAISE(ABORT, '「郵便番号」は数字 3 桁 ＋ 「-」 ＋ 数字 4 桁（123-4567）。')
     WHERE NEW.postal_code NOT GLOB '[0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]';
END;

CREATE TRIGGER trg_company_profile_postal_code_format_update
BEFORE UPDATE OF postal_code ON company_profile
FOR EACH ROW
WHEN NEW.postal_code IS NOT NULL
     AND (typeof(NEW.postal_code) = 'blob'
          OR instr(CAST(NEW.postal_code AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.postal_code AS BLOB)) > 4 * LENGTH(NEW.postal_code)
          OR NEW.postal_code NOT GLOB '[0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]')
BEGIN
    SELECT RAISE(ABORT, '「郵便番号」に使えない字が入っている。')
     WHERE typeof(NEW.postal_code) = 'blob'
        OR instr(CAST(NEW.postal_code AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.postal_code AS BLOB)) > 4 * LENGTH(NEW.postal_code);
    SELECT RAISE(ABORT, '「郵便番号」は数字 3 桁 ＋ 「-」 ＋ 数字 4 桁（123-4567）。')
     WHERE NEW.postal_code NOT GLOB '[0-9][0-9][0-9]-[0-9][0-9][0-9][0-9]';
END;
