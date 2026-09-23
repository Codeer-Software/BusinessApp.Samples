-- 014 経過措置の控除割合（制度ルール。docs/decisions/0069）
--
-- **免税事業者等からの課税仕入れについて、仕入税額相当額の何 % を控除できるか**を持つ。
-- 値を決めるのは法令であり、利用者でもベンダーでもない（docs/12_マスタ台帳）。
-- 配り方は ADR-0020（ベンダーがマイグレーションで配る。画面は閲覧のみ）。
--
-- **種類ごとに表を分ける**（ADR-0069 の決定 1）。税率・相手方別上限額と同じ表に畳まない——
-- 引く鍵も、引く日も、値の形も違う。ここが持つのは「期間だけで引く百分率」の 1 種類である。
--
-- **引く日は課税仕入れの日**（`tax_point`）**そのものである。取引日で代えない**
-- （docs/11 §5 の 4 つの日付の表・§5-1。消税法 30 ①一）。
-- **「基準日」（`tax_point`、無ければ取引日）ではない**——基準日で引くと、
-- **引渡が 9/30・取引日が 10/05 の仕入れが 80% ではなく 70% になる**（境目は 2026・2028・2030・2031 の各 9/30）。
-- 課税期間でも、伝票の会計年度でもない。
--
-- **制度が変わったら、既存行を書き換えず新しい期間の行を INSERT する**（ADR-0069 の決定 5）。
-- だから is_active を持たない——有効・無効を決めるのは期間であり、フラグは 2 つ目の真実になる。
-- **配った値そのものの誤りを直すときだけ UPDATE する**（版の字は変わらないので、
-- 何をいつ直したかはマイグレーションの記録が持つ）。**稼働 DB を手で書き換えても、
-- 次のコミットで `migrate.ps1 -Verify` が正典との差を言う**（VendorRows）。

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
    --   confirmed_on  **法源を最後に確かめた日**（docs/32 §3）。
    --                 **開発者が原文を示して確認した日も含む**——docs/80 §2-1 の出典の列と同じ意味である。
    --                 「URL を開いた日」に限ると、開発者確認で埋めた行に書く日が無くなる
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
    --
    -- **この CHECK 単独では止まらない。** `date()` は読めない値に NULL を返し、**NULL の CHECK は通る**
    -- （2026-09-23 に実測——`('2026-6-1', '2026-5-1')` も `('20261001', …)` も素通りした）。
    -- **止めているのは、下の日付の書式のトリガが ISO を強制していること**である。
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

-- 値の型と、版の字の形を守る。**CHECK ではなくトリガで書く**——
-- この表は 2026-09-23 に配ったあとで足したので、SQLite では CHECK を後から足せない
-- （008・010・011 が同じ理由でトリガにしてある）。
--
-- **なぜ型まで見るか。**
--   ① `CHECK (rate_percent BETWEEN 1 AND 100)` は **REAL の 80.5 を通す**
--      （INTEGER affinity は非可逆な REAL を変換しないので REAL のまま残る。2026-09-23 実測）。
--      ところが読み出し側（DbValue.ToLong）は小数を見つけたら投げるので、
--      **DB が受けた行を読んだ瞬間に計上が止まる**——011 が書いた「DB が広いと、読んだ瞬間に落ちる行が作れる」形である。
--   ② **BLOB は TEXT の列にそのまま入り、UNIQUE でもぶつからない**（010 が 2026-09-09 に実測）。
--      読み出し側（DbValue.ToText）は byte[] を `System.Byte[]` にするので、
--      **その字が版として仕訳に焼かれ、ResolveByVersion は永久に当たらない**。
--
-- **版の字は期間から導く。** 形（<種類>:<valid_from>）を決めただけでは、
-- **版が指す日と、その版が実際に効いた期間が食い違ったまま仕訳に焼ける**。
-- 生成列では導かない（qa/01 H-03）。
--
-- **日付の守りが通す値のときだけ見る。** **SQLite のトリガの発火順は仕様上 undefined** で、
-- 実測では**後に作ったものから鳴る**（011 の注記）ので、**日付が読めない値のときに
-- この版の断りが先に鳴ると、直すべき日付の断りが出ない**（2026-09-23 に実際に出した。
-- `'2460000'`・`'2026-04-32'`・`'2026-13-01'` で踏んだ）。
-- **だから門を上の日付の守りと同じ式にする**——`date(julianday(x)) IS substr(x, 1, 10)`。
-- **比べる側は date() ではなく substr で切る**——`date('2460000')` はユリウス日として読めてしまい、
-- 「読めない値」を「読めた値」に化けさせる。
CREATE TRIGGER trg_transition_purchase_rates_value_shape_insert
BEFORE INSERT ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「控除割合」は 1 以上 100 以下の整数で入れる。')
     WHERE typeof(NEW.rate_percent) <> 'integer';
    SELECT RAISE(ABORT, '「版」「法源」「出典 URL」は文字で入れる。')
     WHERE typeof(NEW.version) <> 'text'
        OR typeof(NEW.legal_basis) <> 'text'
        OR typeof(NEW.source_url) <> 'text';
    SELECT RAISE(ABORT, '「版」は「transition_purchase_rate:<有効期間の開始日>」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS substr(NEW.valid_from, 1, 10)
       AND NEW.version <> 'transition_purchase_rate:' || substr(NEW.valid_from, 1, 10);
END;

CREATE TRIGGER trg_transition_purchase_rates_value_shape_update
BEFORE UPDATE OF valid_from, rate_percent, version, legal_basis, source_url ON transition_purchase_rates
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「控除割合」は 1 以上 100 以下の整数で入れる。')
     WHERE typeof(NEW.rate_percent) <> 'integer';
    SELECT RAISE(ABORT, '「版」「法源」「出典 URL」は文字で入れる。')
     WHERE typeof(NEW.version) <> 'text'
        OR typeof(NEW.legal_basis) <> 'text'
        OR typeof(NEW.source_url) <> 'text';
    SELECT RAISE(ABORT, '「版」は「transition_purchase_rate:<有効期間の開始日>」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS substr(NEW.valid_from, 1, 10)
       AND NEW.version <> 'transition_purchase_rate:' || substr(NEW.valid_from, 1, 10);
END;
