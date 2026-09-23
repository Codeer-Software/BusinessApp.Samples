-- 0039 経過措置の控除割合の表を作る（Designer/ddl/014_transition_rates.sql）
--
-- **行はここで配らない。** データ行の同値の網が入るまで、行を配る DML マイグレーションは書かない
-- （migrations/README。ADR-0020 の帰結）。表だけを先に作り、行は次のマイグレーションで配る。
--
-- 正典の説明は ddl/014 が持つ。ここは配達物なので、同じ説明を写さない。

CREATE TABLE transition_purchase_rates (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    -- 有効期間。**両端を含む**（ADR-0069 の決定 2）。
    -- 法令と国税庁の資料が閉区間で書いている（「令和5年10月1日から令和8年9月30日まで」）ので、
    -- 半開区間に直すと ±1 日の解釈をデータに焼き込むことになる。
    --
    -- **終期は NOT NULL である。** 経過措置は条文が終わりを書いており（附則 53 ① 三は
    -- 令和13年9月30日まで）、「以後ずっと」の行が生まれる余地がない。
    -- **終わった後の行は作らない**——**経過措置が終わったことは「行が無いこと」で表す**。
    -- 0% の行を置くと「経過措置を 0% で適用する」と読め、計上の関門が断る理由を失う。
    valid_from                  DATE NOT NULL,
    valid_to                    DATE NOT NULL,

    -- 控除割合（百分率の整数）。条文は「百分の八十」等と整数で書くので、整数で持つ。
    -- **0 を許さない**（上の終期の注記）。
    rate_percent                INTEGER NOT NULL CHECK (rate_percent BETWEEN 1 AND 100),

    -- 版の字。仕訳明細の applied_rule_version に写す（ADR-0069 の決定 3。docs/10 の I-16）。
    -- 形は <種類>:<valid_from>。連番にしないのは、連番では「どの版か」を人が読んで確かめられないから。
    version                     TEXT NOT NULL UNIQUE,

    -- 法源（ADR-0069 の決定 4）。**3 つとも NOT NULL**——出典の無い制度値を入れられる口を開けない。
    --   legal_basis   条文の指し（docs/80 §3 の記法）
    --   source_url    一次情報の URL
    --   confirmed_on  その URL を最後に開いて確かめた日（docs/32 §3）
    legal_basis                 TEXT NOT NULL,
    source_url                  TEXT NOT NULL,
    confirmed_on                DATE NOT NULL,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない**（ADR-0029。003 と同じ）。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 終わりが始まりより前の期間を作らない。**date() で包む**——
    -- 文字列のまま比べると、時刻の付いた値と付かない値で同じ日が大小逆になる（qa/03 L-12）。
    CHECK (date(valid_from) <= date(valid_to))
);

-- 同じ日から始まる版を 2 つ作らない。**date() で包む**（006 の ux_..._valid_from と同じ理由）——
-- CLB は日付の列へ "2023-10-01 00:00:00" と時刻付きで書く。
CREATE UNIQUE INDEX ux_transition_purchase_rates_valid_from
    ON transition_purchase_rates (date(valid_from));

-- 期間が重なる版を作らない。**重なりは一意索引では止まらない**（始まりが違えば通る）。
-- 006 の trg_partner_invoice_registrations_no_overlap_* と同じ形。
CREATE TRIGGER trg_transition_purchase_rates_no_overlap_insert
BEFORE INSERT ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '有効期間が既にある版と重なっている。')
     WHERE EXISTS (
        SELECT 1 FROM transition_purchase_rates o
         WHERE date(o.valid_from) <= date(NEW.valid_to)
           AND date(NEW.valid_from) <= date(o.valid_to));
END;

CREATE TRIGGER trg_transition_purchase_rates_no_overlap_update
BEFORE UPDATE OF valid_from, valid_to ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '有効期間が既にある版と重なっている。')
     WHERE EXISTS (
        SELECT 1 FROM transition_purchase_rates o
         WHERE o.id <> NEW.id
           AND date(o.valid_from) <= date(NEW.valid_to)
           AND date(NEW.valid_from) <= date(o.valid_to));
END;

-- 日付の列は年月日として読める値だけを受け取る。**条件の字は 011 と同じである**
-- （DateFormatGuardTests の「条件はどこも同じ字である」が突き合わせる）。
--
-- **011 に置かずここに置く。** 011 は「1 つの規則を 1 ファイルにまとめ、いちばん後ろの番号に置く」
-- としているが、**011 より後に生まれた表は 011 の時点で存在しない**。
-- そして **既存 DB への配達は常に末尾へ作る**ので、この表のトリガの作られた順は
-- 「正典でも配達でも 014 の並び」で一致する（MigrationEquivalenceTests が見る）——
-- 011 の注記が守ろうとしている当のものが、ここでは「014 に置く」を正解にする。
CREATE TRIGGER trg_transition_purchase_rates_date_format_insert
BEFORE INSERT ON transition_purchase_rates
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

CREATE TRIGGER trg_transition_purchase_rates_date_format_update
BEFORE UPDATE OF valid_from, valid_to, confirmed_on ON transition_purchase_rates
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
