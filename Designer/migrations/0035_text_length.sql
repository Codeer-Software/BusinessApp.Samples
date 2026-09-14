--------------------------------------------------------------------------------
-- 0035 文字の欄の上限（docs/12 §2-2）
--
-- マスタと取引先の文字の欄に上限を置く。**関門が本体で、ここは最後の守り**である。
--
-- **既にある行は見ない**（トリガは追加と更新のときだけ鳴る）ので、
-- **適用は必ず成功する**。長すぎる行が残っていても黙って残るので、適用前に数える。
--
--   SELECT 'accounts.name'      AS 欄, COUNT(*) FROM accounts          WHERE LENGTH(name) > 30
--   UNION ALL SELECT 'sub_accounts.name',  COUNT(*) FROM sub_accounts   WHERE LENGTH(name) > 30
--   UNION ALL SELECT 'departments.name',   COUNT(*) FROM departments    WHERE LENGTH(name) > 30
--   UNION ALL SELECT 'tax_categories.name',COUNT(*) FROM tax_categories WHERE LENGTH(name) > 30
--   UNION ALL SELECT 'partners.name',      COUNT(*) FROM partners       WHERE LENGTH(name) > 100
--   UNION ALL SELECT 'partners.name_kana', COUNT(*) FROM partners       WHERE LENGTH(name_kana) > 100
--   UNION ALL SELECT 'partners.address',   COUNT(*) FROM partners       WHERE LENGTH(address) > 200;
--
-- **2026-09-14 に稼働 DB で数えて 7 欄とも 0 件。**
--------------------------------------------------------------------------------

CREATE TRIGGER trg_accounts_name_length_insert
BEFORE INSERT ON accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_accounts_name_length_update
BEFORE UPDATE OF name ON accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_sub_accounts_name_length_insert
BEFORE INSERT ON sub_accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「補助科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「補助科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_sub_accounts_name_length_update
BEFORE UPDATE OF name ON sub_accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「補助科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「補助科目名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_departments_name_length_insert
BEFORE INSERT ON departments
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「部門名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「部門名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_departments_name_length_update
BEFORE UPDATE OF name ON departments
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「部門名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「部門名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_tax_categories_name_length_insert
BEFORE INSERT ON tax_categories
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「税区分名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「税区分名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_tax_categories_name_length_update
BEFORE UPDATE OF name ON tax_categories
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「税区分名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「税区分名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_partners_name_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 100
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「取引先名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「取引先名」は 100 文字以内。')
     WHERE LENGTH(NEW.name) > 100;
END;

CREATE TRIGGER trg_partners_name_length_update
BEFORE UPDATE OF name ON partners
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 100
          OR typeof(NEW.name) = 'blob'
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「取引先名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「取引先名」は 100 文字以内。')
     WHERE LENGTH(NEW.name) > 100;
END;

CREATE TRIGGER trg_partners_name_kana_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 100
          OR typeof(NEW.name_kana) = 'blob'
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「カナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「カナ」は 100 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 100;
END;

CREATE TRIGGER trg_partners_name_kana_length_update
BEFORE UPDATE OF name_kana ON partners
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 100
          OR typeof(NEW.name_kana) = 'blob'
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「カナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「カナ」は 100 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 100;
END;

CREATE TRIGGER trg_partners_address_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.address IS NOT NULL
     AND (LENGTH(NEW.address) > 200
          OR typeof(NEW.address) = 'blob'
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「所在地」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address);
    SELECT RAISE(ABORT, '「所在地」は 200 文字以内。')
     WHERE LENGTH(NEW.address) > 200;
END;

CREATE TRIGGER trg_partners_address_length_update
BEFORE UPDATE OF address ON partners
FOR EACH ROW
WHEN NEW.address IS NOT NULL
     AND (LENGTH(NEW.address) > 200
          OR typeof(NEW.address) = 'blob'
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「所在地」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address);
    SELECT RAISE(ABORT, '「所在地」は 200 文字以内。')
     WHERE LENGTH(NEW.address) > 200;
END;
