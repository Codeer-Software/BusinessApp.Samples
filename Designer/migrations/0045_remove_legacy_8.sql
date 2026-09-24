-- 旧税率 8%（legacy_8）をスコープから外す（docs/05 の Q-54。開発者の条件つきの決定に Claude が照らして判断した）。
--   税区分の 2 行（TS8・TP8）と税率の表の 1 行（tax_rate:legacy_8:2019-10-01）を消し、
--   tax_categories と tax_rates の rate_kind の CHECK から legacy_8 を外す。
--
-- **CHECK を変えるのは作り直しでしか行えない**（migrations/README のレシピ）。
-- **行を消すのは、この往復（seed と migrations が同じ行に着くこと）に当たらない既存行の移行である**——
-- 1 行ずつ特定できる条件で書く（コード・名前・税率区分、版の字）。
-- **名前は利用者が直せる列である**——名前を直した環境では DELETE が空振りし、書き戻しが CHECK に当たって丸ごと巻き戻る。
-- そのときは下の確認の 4 つ目が数える（2026-09-24 の自己レビューで分かった。適用したあとだったので、DELETE の条件は当てたときのまま残す）。
-- **マスタと制度ルールの行を消すのは、docs/12 §1 の「削除はさせない」の例外である**（条件は同じ節）。
--
-- **適用前の確認**——次の 5 つのうち 1 つでも 1 件以上あれば、適用しない（**数えるのは適用する人**）。
-- 1・2 つ目は数え忘れても COMMIT で（遅延外部キー）、4・5 つ目は書き戻しで（CHECK）落ちて丸ごと巻き戻る。
-- **3 つ目は数え忘れても落ちない**——外部キーが無いので、消えた版を指したまま明細が残る。
-- 開発機では（sql CLI）、適用の前（2026-09-24）に 1・2 つ目と、4 つ目より粗い形（code だけを条件にした形）を数え、どれも 0 だった。
-- 3 つ目は適用のあと（2026-09-25）に数えて 0（この移行は明細に触れない）。5 つ目は数えていない——
-- 適用の前の migrate.ps1 -Verify が、税率の表の行が配った 3 行と同値であることを見ていた。
--   消す税区分を使う明細・勘定科目の既定（外部キーの子）:
--   SELECT COUNT(*) FROM journal_lines WHERE tax_category_id IN (SELECT id FROM tax_categories WHERE rate_kind = 'legacy_8');
--   SELECT COUNT(*) FROM accounts WHERE default_tax_category_id IN (SELECT id FROM tax_categories WHERE rate_kind = 'legacy_8');
--   **消す税率の版を記録した明細**（journal_lines.applied_rule_version。I-16）:
--   SELECT COUNT(*) FROM journal_lines WHERE applied_rule_version LIKE 'tax_rate:legacy_8:%';
--   **下の DELETE の消し漏れ**（DELETE の条件の裏返し）:
--   SELECT COUNT(*) FROM tax_categories WHERE rate_kind = 'legacy_8'
--      AND NOT ((code = 'TS8' AND name = '課税売上（旧税率8%）') OR (code = 'TP8' AND name = '課税仕入（旧税率8%）'));
--   SELECT COUNT(*) FROM tax_rates WHERE rate_kind = 'legacy_8' AND version <> 'tax_rate:legacy_8:2019-10-01';

PRAGMA defer_foreign_keys = ON;

DELETE FROM tax_categories WHERE code = 'TS8' AND name = '課税売上（旧税率8%）' AND rate_kind = 'legacy_8';
DELETE FROM tax_categories WHERE code = 'TP8' AND name = '課税仕入（旧税率8%）' AND rate_kind = 'legacy_8';
DELETE FROM tax_rates WHERE version = 'tax_rate:legacy_8:2019-10-01' AND rate_kind = 'legacy_8';

-- --- tax_categories を作り直す（正典: Designer/ddl。トリガと索引は正典の各ファイルから逐語で写した） ---
CREATE TABLE tax_categories_rebuild AS SELECT * FROM tax_categories;
CREATE TABLE tax_categories_rebuild_seq AS SELECT seq FROM sqlite_sequence WHERE name = 'tax_categories';
DROP TABLE tax_categories;

CREATE TABLE tax_categories (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    code                        TEXT NOT NULL UNIQUE,
    name                        TEXT NOT NULL,

    -- 課税区分（docs/11 §1）。
    --   taxable_sales        課税売上
    --   taxable_purchase     課税仕入
    --   non_taxable_sales    非課税売上
    --   non_taxable_purchase 非課税仕入
    --   export_exempt        免税（輸出）＝売上のみ
    --   out_of_scope         不課税（対象外）
    -- 「未設定」を NULL と out_of_scope の 2 通りで表さないため NOT NULL にする。
    --
    -- **非課税にも売上／仕入の軸を通してある。** 課税だけ分けて非課税を 1 つに潰すと、
    -- 課税売上割合の分母（課税＋免税＋非課税の売上高）を税区分だけでは作れず、
    -- 勘定科目の科目区分に頼ることになる（docs/11 §7）。税の軸は税区分で完結させる。
    taxation_type               TEXT NOT NULL CHECK (taxation_type IN (
                                    'taxable_sales', 'taxable_purchase',
                                    'non_taxable_sales', 'non_taxable_purchase',
                                    'export_exempt', 'out_of_scope')),

    -- 税率の「種類」。**税率の数値はここに持たない。**
    -- 利用者が選ぶのは「標準税率か軽減税率か」であって、その日に何 % かは制度ルールが決める。
    -- 名前に "10%" と書いてしまうと、税率が変わった日にマスタ名が嘘になる。
    --   standard  標準税率
    --   reduced   軽減税率
    --   NULL      税率の概念がない区分（非課税・免税・対象外）
    -- **旧税率 8% はスコープの外である**（docs/11 §1-1-1）。
    rate_kind                   TEXT CHECK (rate_kind IN ('standard', 'reduced')),

    -- 入力時の初期値としての用途区分。明細の値が正であり、
    -- 「値が入っていない行の穴埋め」には使わない（docs/11 §1）。
    -- 値を for_... にしてあるのは、課税区分の taxable_sales と**同じ値が別の意味で 2 か所に現れる**のを
    -- 避けるためである。取り違えても値が同じだと実行時にも通ってしまう。
    default_tax_treatment       TEXT CHECK (default_tax_treatment IN (
                                    'for_taxable_sales', 'common', 'for_exempt_sales')),

    is_active                   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    display_order               INTEGER,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0029）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 課税区分と税率区分の整合。課税でない区分に税率区分は付かず、課税の区分には必ず付く。
    CHECK (taxation_type IN ('taxable_sales', 'taxable_purchase') OR rate_kind IS NULL),
    CHECK (taxation_type NOT IN ('taxable_sales', 'taxable_purchase') OR rate_kind IS NOT NULL)
);

INSERT INTO tax_categories (id, code, name, taxation_type, rate_kind, default_tax_treatment, is_active, display_order, created_at, updated_at, creator, updater, optimistic_locking)
    SELECT id, code, name, taxation_type, rate_kind, default_tax_treatment, is_active, display_order, created_at, updated_at, creator, updater, optimistic_locking FROM tax_categories_rebuild;
DROP TABLE tax_categories_rebuild;

-- ddl/005_journals.sql
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

-- ddl/005_journals.sql
CREATE TRIGGER trg_tax_categories_no_replace_used_insert
BEFORE INSERT ON tax_categories
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている税区分を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.tax_category_id = NEW.id AND e.status = 'posted');
END;

-- ddl/005_journals.sql
CREATE TRIGGER trg_tax_categories_no_replace_used_update
BEFORE UPDATE OF id ON tax_categories
FOR EACH ROW WHEN NEW.id IS NOT OLD.id
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細が使っている税区分を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.tax_category_id IN (OLD.id, NEW.id) AND e.status = 'posted');
END;

-- ddl/008_master_code_format.sql
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

-- ddl/008_master_code_format.sql
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

-- ddl/008_master_code_format.sql
CREATE UNIQUE INDEX ux_tax_categories_code_nocase ON tax_categories (code COLLATE NOCASE);

-- ddl/012_text_length.sql
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

-- ddl/012_text_length.sql
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

UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM tax_categories_rebuild_seq))
 WHERE name = 'tax_categories' AND EXISTS (SELECT 1 FROM tax_categories_rebuild_seq);
INSERT INTO sqlite_sequence (name, seq)
    SELECT 'tax_categories', seq FROM tax_categories_rebuild_seq
    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'tax_categories');
DROP TABLE tax_categories_rebuild_seq;

-- --- tax_rates を作り直す（正典: Designer/ddl。トリガと索引は正典の各ファイルから逐語で写した） ---
CREATE TABLE tax_rates_rebuild AS SELECT * FROM tax_rates;
CREATE TABLE tax_rates_rebuild_seq AS SELECT seq FROM sqlite_sequence WHERE name = 'tax_rates';
DROP TABLE tax_rates;

CREATE TABLE tax_rates (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    -- 税率区分。**003 の tax_categories.rate_kind と同じ語彙である**
    -- （TaxRateConstraintTests が 2 つの CHECK を突き合わせる——**片方だけ足すと静かに引けない行ができる**）。
    --   standard  標準税率
    --   reduced   軽減税率
    -- **ここは NOT NULL である。** 税率の概念がない区分（非課税・免税・対象外）は、
    -- 税区分マスタ側で rate_kind を NULL にして「この表を引かない」と表す。
    rate_kind                   TEXT NOT NULL CHECK (rate_kind IN ('standard', 'reduced')),

    -- 有効期間。**両端を含む**（ADR-0069 の決定 2。014 と同じ）。
    --
    -- **終期は NULL を許す。ここが 014 と違う。** 消税法 29 も地方税法 72 の 83 も期限を書いていない
    -- （税率リサーチ §1）。
    -- **NULL は「終期なし」であって「未設定」ではない**——この表には未設定の行を入れない。
    valid_from                  DATE NOT NULL,
    valid_to                    DATE,

    -- 国税の税率。**万分率の整数**で持つ（百分の七・八 ＝ 万分の七八〇 ＝ 780。理由は docs/11 §1-1）。
    --
    -- **typeof() を CHECK に入れてある。** INTEGER affinity は非可逆な REAL を変換しないので、
    -- **`BETWEEN 1 AND 10000` だけでは 780.5 が REAL のまま居座る**（2026-09-23 に 014 で実測）。
    national_rate_per_10000     INTEGER NOT NULL CHECK (
                                    typeof(national_rate_per_10000) = 'integer'
                                    AND national_rate_per_10000 BETWEEN 1 AND 10000),

    -- 地方消費税の税率。**分数で持つ**（七十八分の二十二 → 22 / 78。理由は docs/11 §1-1）。
    -- **分子と分母の関係は下の表レベルの CHECK が見る**（列の CHECK では他の列を見られない）。
    local_numerator             INTEGER NOT NULL CHECK (
                                    typeof(local_numerator) = 'integer' AND local_numerator > 0),
    -- **分母にも帯を置く**（国税の率と同じ作法）。**桁の溢れではない**——
    -- SQLite の整数は 64 ビットで、読み出し側も long で掛ける（TaxRate の不変条件）。
    -- **置くのは「制度値として在りえない値」を入り口で断るため**である
    -- （地方消費税の分母は 78・63 で、桁が 1 つ増えることすら制度上は起きていない）。
    local_denominator           INTEGER NOT NULL CHECK (
                                    typeof(local_denominator) = 'integer'
                                    AND local_denominator BETWEEN 1 AND 10000),

    -- **合計税率（10%・8%）は持たない。導く**（docs/11 §1-1。TaxRateTests が配っている区分をどれも検算する）。
    -- **割り切れる行だけを入れる**のは下の表レベルの CHECK が見る。

    -- 版の文字列（仕訳明細の applied_rule_version に写す値。ADR-0069 の決定 3。docs/10 の I-16）。
    -- **形は <種類>:<税率区分>:<valid_from>**——**ここは税率区分も鍵なので、
    -- 版の字にも入れないと区分どうしの版が同じ字になる**（014 は日付だけで引くので種類と日付で足りた）。
    --
    -- **版の字は期間と区分から導く**（014 と同じ。生成列にはしない——qa/01 H-03）。
    -- **valid_from が読めない値のときも、この CHECK は NULL にならない**——NOT NULL の列なので
    -- substr() は必ず文字を返し、真か偽になる。**だからこの CHECK は日付の守りにならない**
    -- （'20191001' から導いた版の字は、この CHECK を満たしてしまう）。
    -- **読めない日付を断るのは下の日付の書式のトリガだけ**で、
    -- **BEFORE トリガは CHECK より先に走る**ので、断りの文言もそちらが出る。
    version                     TEXT NOT NULL UNIQUE CHECK (
                                    typeof(version) = 'text'
                                    AND version = 'tax_rate:' || rate_kind || ':' || substr(valid_from, 1, 10)),

    -- 法源（ADR-0069 の決定 4）。**3 つとも NOT NULL**——出典の無い制度値を入れられる口を開けない。
    -- 意味は 014 の同名の列と同じ（confirmed_on は「法源を最後に確かめた日」で、
    -- **開発者が原文を示して確認した日も含む**）。

    legal_basis                 TEXT NOT NULL CHECK (typeof(legal_basis) = 'text'),
    source_url                  TEXT NOT NULL CHECK (typeof(source_url) = 'text'),
    confirmed_on                DATE NOT NULL,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない**（ADR-0029。003 と同じ）。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 終わりが始まりより前の期間を作らない。**date() で包む**（014 と同じ。qa/03 L-12）。
    -- **valid_to が NULL の行では、この CHECK は NULL になって通る**——「終期なし」を許すためである。
    --
    -- **この CHECK 単独では止まらない**（014 の同じ注記）。date() は読めない値に NULL を返し、
    -- **NULL の CHECK は通る**。**止めているのは、下の日付の書式のトリガが ISO を強制していること**である。
    CHECK (date(valid_from) <= date(valid_to)),

    -- 地方消費税の税率は、消費税額に対する**真分数**である（22/78・17/63）。
    -- **分子と分母を取り違えた行を止める**——78/22 は型も正も版の字も日付も全部通り、
    -- **地方税額が国税の 3.5 倍になる**（2026-09-23 の自己レビューで数えた）。
    -- **将来 1 以上の分数を定める改正があれば、この CHECK ごと差し替える**
    -- （制度ルールの形が変わる回はどのみちスキーマ変更である。ADR-0020）。
    CHECK (local_numerator < local_denominator),

    -- **合計税率が万分率の整数になること。** 国税 ×（分母＋分子）÷ 分母 が割り切れる行だけを入れる。
    -- **これが「合計税率を持たない」ことの土台である**（理由は docs/11 §1-1）。
    -- (800, 22, 78) は他の CHECK を全部通るが、ここで断られる（800 × 100 ÷ 78 = 1025.64…）。
    CHECK (national_rate_per_10000 * (local_denominator + local_numerator) % local_denominator = 0)
);

INSERT INTO tax_rates (id, rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator, local_denominator, version, legal_basis, source_url, confirmed_on, created_at, updated_at, creator, updater, optimistic_locking)
    SELECT id, rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator, local_denominator, version, legal_basis, source_url, confirmed_on, created_at, updated_at, creator, updater, optimistic_locking FROM tax_rates_rebuild;
DROP TABLE tax_rates_rebuild;

-- ddl/015_tax_rates.sql
CREATE UNIQUE INDEX ux_tax_rates_kind_valid_from
    ON tax_rates (rate_kind, date(valid_from));

-- ddl/015_tax_rates.sql
CREATE TRIGGER trg_tax_rates_no_overlap_insert
BEFORE INSERT ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '同じ税率区分で、有効期間が既にある版と重なっている。')
     WHERE date(julianday(NEW.valid_from)) IS substr(NEW.valid_from, 1, 10)
       AND EXISTS (
        SELECT 1 FROM tax_rates o
         WHERE o.rate_kind = NEW.rate_kind
           AND (o.valid_to IS NULL OR date(o.valid_to) >= date(NEW.valid_from))
           AND (NEW.valid_to IS NULL OR date(NEW.valid_to) >= date(o.valid_from)));
END;

-- ddl/015_tax_rates.sql
CREATE TRIGGER trg_tax_rates_no_overlap_update
BEFORE UPDATE OF rate_kind, valid_from, valid_to ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '同じ税率区分で、有効期間が既にある版と重なっている。')
     WHERE date(julianday(NEW.valid_from)) IS substr(NEW.valid_from, 1, 10)
       AND EXISTS (
        SELECT 1 FROM tax_rates o
         WHERE o.id <> NEW.id
           AND o.rate_kind = NEW.rate_kind
           AND (o.valid_to IS NULL OR date(o.valid_to) >= date(NEW.valid_from))
           AND (NEW.valid_to IS NULL OR date(NEW.valid_to) >= date(o.valid_from)));
END;

-- ddl/015_tax_rates.sql
CREATE TRIGGER trg_tax_rates_date_format_insert
BEFORE INSERT ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「有効期間の開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR (NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_from) <= 27))
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「有効期間の終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_to)) IS NOT substr(NEW.valid_to, 1, 10)
        OR (NEW.valid_to NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_to NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_to GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_to, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_to) <= 27))
        OR LENGTH(CAST(NEW.valid_to AS BLOB)) <> LENGTH(NEW.valid_to);
    SELECT RAISE(ABORT, '「確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR (NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.confirmed_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.confirmed_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.confirmed_on) <= 27))
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
END;

-- ddl/015_tax_rates.sql
CREATE TRIGGER trg_tax_rates_date_format_update
BEFORE UPDATE OF valid_from, valid_to, confirmed_on ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「有効期間の開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR (NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_from) <= 27))
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「有効期間の終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_to)) IS NOT substr(NEW.valid_to, 1, 10)
        OR (NEW.valid_to NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_to NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_to GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_to, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_to) <= 27))
        OR LENGTH(CAST(NEW.valid_to AS BLOB)) <> LENGTH(NEW.valid_to);
    SELECT RAISE(ABORT, '「確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR (NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.confirmed_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.confirmed_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.confirmed_on) <= 27))
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
END;

-- ddl/016_vendor_row_no_replace.sql
CREATE TRIGGER trg_tax_rates_no_replace_insert
BEFORE INSERT ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '税率の行を、既にある行の識別子へ被せられない。')
     WHERE EXISTS (SELECT 1 FROM tax_rates o WHERE o.id = NEW.id);
END;

-- ddl/016_vendor_row_no_replace.sql
CREATE TRIGGER trg_tax_rates_no_replace_update
BEFORE UPDATE ON tax_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '税率の行を、既にある行の識別子へ被せられない。')
     WHERE EXISTS (SELECT 1 FROM tax_rates o WHERE o.id <> OLD.id AND o.id = NEW.id);
END;

UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM tax_rates_rebuild_seq))
 WHERE name = 'tax_rates' AND EXISTS (SELECT 1 FROM tax_rates_rebuild_seq);
INSERT INTO sqlite_sequence (name, seq)
    SELECT 'tax_rates', seq FROM tax_rates_rebuild_seq
    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'tax_rates');
DROP TABLE tax_rates_rebuild_seq;
