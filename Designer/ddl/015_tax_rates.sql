-- 015 税率（制度ルール。docs/decisions/0069・docs/11 §1-1）
--
-- **税率区分ごとに、国税の率と地方消費税の分数を、有効期間つきの行で持つ**（いま配っている行の期間の出どころは税率リサーチ §1-3——版の一覧から読んだ始期）。
-- **決めていないのは、経過措置で改正前の税率を使う取引をどう持つか**——期間を「その日の取引にこの区分を使うなら何 % か」と読み替えるか（docs/11 §1-1-1。旧税率 8% はスコープの外）。
-- 値を決めるのは法令であり、利用者でもベンダーでもない（docs/12_マスタ台帳）。
-- 配り方は ADR-0020（ベンダーがマイグレーションで配る。画面は閲覧のみ）。
--
-- **種類ごとに表を分ける**（ADR-0069 の決定 1）。014（経過措置の控除割合）と同じ表に畳まない——
-- **鍵も終わり方も値の形も違う**。**なぜこの形かは docs/11 §1-1 の表が持つ。ここに写さない。**
--
-- **引く日は tax_point**（課税資産の譲渡等をした日・課税仕入れを行った日）。
-- **取引日でも課税期間でもない**（docs/11 §5-2 の 4 つの日付の表・§1-1）。
-- **返還等の専用の税区分の行だけは例外で、まだ決めていない**（docs/11 の保留リスト）。
--
-- **条文と、切り替わった日は docs/research/2026-09-23_消費税の税率と地方消費税の税率.md が持つ。ここに写さない。**

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

-- 同じ区分で、同じ日から始まる版を 2 つ作らない。**date() で包む**（014 と同じ理由）——
-- CLB は日付の列へ "2019-10-01 00:00:00" と時刻付きで書く。
-- **区分を鍵に含める**——含めないと、区分どうしが同じ日から始められない。
--
-- **この索引が断る行は、下の重なりのトリガも断る**（始まりが同じなら必ず重なる）。
-- **残してあるのは 2 つ目の網としてである**——**重なりのトリガを 1 本外した日に、索引だけは残る**
-- （014 と同じ形）。
--
-- **ただし「独立した 2 枚」ではない。** 索引も重なりのトリガも `date()` に寄りかかっており、
-- **`date()` が NULL を返す値では同時に無力になる**——一意索引は NULL 同士を別物として通し、
-- 重なりの条件は NULL になって EXISTS が偽になる。
-- **その値を止めているのは、下の日付の書式のトリガ 1 本だけである**
-- （2026-09-23 の自己レビュー。DateFormatGuardTests のクラス注記が壊れ方①②として名指ししている形）。
CREATE UNIQUE INDEX ux_tax_rates_kind_valid_from
    ON tax_rates (rate_kind, date(valid_from));

-- 同じ区分の中で、期間が重なる版を作らない。**重なりは一意索引では止まらない**（始まりが違えば通る）。
-- **終期なし（NULL）は「無限の未来まで」として比べる**——素直に date(o.valid_to) と書くと
-- NULL 同士の比較で条件全体が NULL になり、**重なりを 1 本も見つけずに通す**。
--
-- **開始日が年月日として読めるときだけ重なりを見る**（WHERE の 1 行目がその先行条件である）。
-- **配っている 2 行は終期が無い**ので、この先行条件が無いと
-- **開始日が読めない行でもこのトリガの条件が真になり、日付の書式のトリガと同時に失敗する条件に入る**
-- ——**SQLite のトリガの発火順は仕様上 undefined** なので、
-- **直すべき日付のエラーメッセージではなく、重なりのほうが返る日がありうる**（qa/01 H-12。014 が踏んだ形）。
-- **条件の字は 014 の値の形のトリガと同じである**——`date(julianday(x)) IS substr(x, 1, 10)`。
-- **ただし、この先行条件を外しても、いまの SQLite では同じエラーメッセージが返る**
-- （日付のトリガが後に作られている分だけ先に失敗するため。2026-09-23 に実測）
-- ——**発火順が変わった日のための保険であり、テストでは観測できない**。
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

-- 日付の列は年月日として読める値だけを受け取る。**条件の字は 011・014 と同じである**
-- （DateFormatGuardTests の「条件はどこも同じ字である」が突き合わせる）。
-- 011 ではなくここに置く理由も 014 と同じ（011 より後に生まれた表は 011 の時点で存在しない）。
--
-- **valid_to が NULL の行は、この条件を素通りする。** date(NULL) も substr(NULL, 1, 10) も NULL で、
-- `NULL IS NOT NULL` は偽、残りの項は NULL なので、WHERE 全体が NULL になって RAISE が鳴らない
-- ——**終期なしを許すために、条件の字を変えずに済む**。
--
-- **値の形（型・分数の関係・版の字）は CHECK が見る。トリガにしない。** 015 は新しい表なので
-- 後付けにならず、**SQLite は BEFORE トリガを走らせてから CHECK を見る**ので、
-- **日付が読めない値では、必ずこの断りが先に鳴る**
-- （014 は両方トリガで、鳴る順が入れ替わる事故を 2026-09-23 に踏んだ）。
--
-- **代わりに 1 つ弱いところがある**——**`INSERT OR IGNORE` は CHECK 違反の行を黙って飛ばす**
-- （トリガの `RAISE(ABORT)` は飛ばせない。2026-09-23 に実測）。
-- **この表を冪等に配るときは `OR IGNORE` を使わず、`NOT EXISTS` で包む**（seed/006 がそうしてある）。
-- **飛ばされた行は `migrate.ps1 -Verify` と MigrationEquivalenceTests が「行が足りない」と言う**
-- ので、静かに終わるのは配った本人のところまでである。
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
