--------------------------------------------------------------------------------
-- 文字の欄の上限（docs/12 §2-2。2026-09-13 の決着）
--
-- **関門が本体・ここは最後の守り**である（§2-1 のコードと同じ 2 段）。
-- アプリを迂回する経路（CLI・取込・手打ちの SQL）にも効かせるために置く。
--
-- **上限は 3 か所にある**——C# の `MasterTextLength`・このトリガ・デザインのプレースホルダ。
-- **DDL が SQL テキスト、デザインが JSON で、どちらも C# の定数を読めない**ので已むを得ない
-- （docs/20 §4）。**3 者の一致は `FieldLengthConsistencyTests` が毎回突き合わせる。**
--
-- **`LENGTH()` は TEXT なら符号点で数える**（docs/12 §2-2 が「符号点で数える」と決めた形）。
-- **C# の `string.Length` は UTF-16 の符号単位を数える**ので、
-- 𠮟 や 🙂 のような字で 2 になり、**関門が断った値を DDL が通す**——
-- だから C# 側も符号点で数えている（`MasterTextLength.Count`）。
--
-- **数えられない値は 3 通りとも断る。**
--   1. **BLOB**——`LENGTH()` がバイト数を返すので、短い BLOB は素通りする。
--   2. **NUL を含む TEXT**——`LENGTH()` は途中の U+0000 で止まるので、後ろはいくらでも隠せる。
--      **`instr(CAST(x AS BLOB), x'00') > 0` で直に探す。**
--      **バイト数と符号点の比では見つからない**——`'A'×30 || NUL || 'B'×60` は
--      91 バイト・可視 30 符号点で、`4 × 30 = 120` に届かないからである
--      （自己レビューのラウンド 94。2 人が独立に指摘した）。
--   3. **UTF-8 として壊れた TEXT**——`バイト数 > 4 × 符号点`。
--      1 符号点は高々 4 バイトなので、超えるなら符号化が壊れている。
--
-- **ALTER TABLE で CHECK は足せない**ので CREATE TRIGGER で配る（008・011 と同じ判断）。
-- **追加と更新の 2 本 1 組。** 更新は `BEFORE UPDATE OF <列>` で、その列を触ったときだけ鳴る
-- ——**上限を決める前から入っていた長い行が、別の欄すら直せなくなる**のを避けるためである。
--
-- **呼び名は画面のラベルと違ってよい。** この文言が届くのは画面を通らない経路の人なので、
-- 「カナ」ではどの表か決まらない（画面には 1 つしか無いが、DB には 3 つある）。
--
-- **この表に無い文字の欄**（勘定科目・補助科目のカナ、自社情報、登録の公表名、
-- 伝票の摘要と明細の内容）については docs/12 §2-2 を見よ。
--------------------------------------------------------------------------------

CREATE TRIGGER trg_accounts_name_length_insert
BEFORE INSERT ON accounts
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 30
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「補助科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「補助科目名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「部門名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「部門名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「税区分名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「税区分名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name);
    SELECT RAISE(ABORT, '「税区分名」は 30 文字以内。')
     WHERE LENGTH(NEW.name) > 30;
END;

CREATE TRIGGER trg_fiscal_years_label_length_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
WHEN NEW.label IS NOT NULL
     AND (LENGTH(NEW.label) > 30
          OR typeof(NEW.label) = 'blob'
          OR instr(CAST(NEW.label AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label))
BEGIN
    SELECT RAISE(ABORT, '「年度名」に使えない字が入っている。')
     WHERE typeof(NEW.label) = 'blob'
        OR instr(CAST(NEW.label AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label);
    SELECT RAISE(ABORT, '「年度名」は 30 文字以内。')
     WHERE LENGTH(NEW.label) > 30;
END;

CREATE TRIGGER trg_fiscal_years_label_length_update
BEFORE UPDATE OF label ON fiscal_years
FOR EACH ROW
WHEN NEW.label IS NOT NULL
     AND (LENGTH(NEW.label) > 30
          OR typeof(NEW.label) = 'blob'
          OR instr(CAST(NEW.label AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label))
BEGIN
    SELECT RAISE(ABORT, '「年度名」に使えない字が入っている。')
     WHERE typeof(NEW.label) = 'blob'
        OR instr(CAST(NEW.label AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.label AS BLOB)) > 4 * LENGTH(NEW.label);
    SELECT RAISE(ABORT, '「年度名」は 30 文字以内。')
     WHERE LENGTH(NEW.label) > 30;
END;

CREATE TRIGGER trg_partners_name_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.name IS NOT NULL
     AND (LENGTH(NEW.name) > 100
          OR typeof(NEW.name) = 'blob'
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「取引先名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name AS BLOB)) > 4 * LENGTH(NEW.name))
BEGIN
    SELECT RAISE(ABORT, '「取引先名」に使えない字が入っている。')
     WHERE typeof(NEW.name) = 'blob'
        OR instr(CAST(NEW.name AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「取引先のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「取引先のカナ」は 100 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 100;
END;

CREATE TRIGGER trg_partners_name_kana_length_update
BEFORE UPDATE OF name_kana ON partners
FOR EACH ROW
WHEN NEW.name_kana IS NOT NULL
     AND (LENGTH(NEW.name_kana) > 100
          OR typeof(NEW.name_kana) = 'blob'
          OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana))
BEGIN
    SELECT RAISE(ABORT, '「取引先のカナ」に使えない字が入っている。')
     WHERE typeof(NEW.name_kana) = 'blob'
        OR instr(CAST(NEW.name_kana AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.name_kana AS BLOB)) > 4 * LENGTH(NEW.name_kana);
    SELECT RAISE(ABORT, '「取引先のカナ」は 100 文字以内。')
     WHERE LENGTH(NEW.name_kana) > 100;
END;

CREATE TRIGGER trg_partners_address_length_insert
BEFORE INSERT ON partners
FOR EACH ROW
WHEN NEW.address IS NOT NULL
     AND (LENGTH(NEW.address) > 200
          OR typeof(NEW.address) = 'blob'
          OR instr(CAST(NEW.address AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「所在地」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR instr(CAST(NEW.address AS BLOB), x'00') > 0
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
          OR instr(CAST(NEW.address AS BLOB), x'00') > 0
          OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address))
BEGIN
    SELECT RAISE(ABORT, '「所在地」に使えない字が入っている。')
     WHERE typeof(NEW.address) = 'blob'
        OR instr(CAST(NEW.address AS BLOB), x'00') > 0
        OR LENGTH(CAST(NEW.address AS BLOB)) > 4 * LENGTH(NEW.address);
    SELECT RAISE(ABORT, '「所在地」は 200 文字以内。')
     WHERE LENGTH(NEW.address) > 200;
END;
