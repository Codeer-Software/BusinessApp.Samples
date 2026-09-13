-- 011 日付の列は年月日として読める値だけを受け取る
--
-- **置き場所。** ここだけ「対象の表の DDL ファイルの末尾に足す」（README）を外している。
-- fiscal_years は 008 に既にトリガを持つ（trg_fiscal_years_code_format_*）ので、
-- 日付のトリガを 002 の末尾へ足すと正典の作成順は
--   [date_format_insert, date_format_update, code_format_insert, code_format_update]
-- になるが、既存 DB への配達は常に末尾へ作るので
--   [code_format_insert, code_format_update, date_format_insert, date_format_update]
-- になり、**同じ表のトリガの作られた順が正典と食い違う**——README の規則が守ろうとしている
-- 当のものが、ここでは「002 の末尾」を禁じる側に働く（MigrationEquivalenceTests が見る）。
-- **だから 008 と同じ形にした**——1 つの規則を 1 ファイルにまとめ、いちばん後ろの番号に置く。
-- 008 も、6 表のうち衝突するのは一部だけなのに全部をまとめてある。
--   fiscal_years                   008 の code_format → 011 の date_format
--   accounting_periods             011 の date_format だけ
--   journal_entries                005 の 12 本 → 011 の date_format
--   journal_lines                   005 の 6 本 → 011 の date_format
--   partner_invoice_registrations  006 の no_overlap → 011 の date_format
--
-- **SQLite の日付は文字列である。** 列を DATE で宣言しても affinity は NUMERIC でしかなく、
-- '20260401' も '2026-6-1' も 'now' もそのまま入る。そして入った瞬間に守りが 3 つ同時に効かなくなる。
--
--   ① date(x) は読めない値に NULL を返す。**一意索引は NULL 同士を別物として扱う**ので、
--      ux_partner_invoice_registrations_valid_from（partner_id, date(valid_from)）は
--      同じ日から始まる登録の重複を止めない（2026-09-13 実測）
--   ② 期間の重なりを見るトリガ（trg_partner_invoice_registrations_no_overlap_*）は
--      比較が全部 NULL になり、1 本も鳴らない
--   ③ 文字列のまま比べている CHECK（002 の 2 本・006 の 1 本）は辞書順で判定するので、
--      '2026-06-30' <= '2026-6-1' が真になり、**終わりが始まりより前の期間が通る**
--
-- **直し方はトリガである。** SQLite は ALTER TABLE で CHECK を足せず表の作り直しが要るが、
-- 入口で拒むトリガなら CREATE TRIGGER だけで配れる（008 と同じ判断）。
-- **日付の列が ISO で揃えば、①②③ はどれも直る**——③ は辞書順と暦順が一致するからである。
--
-- **条件は 1 列につき 3 つ。**
--
--   date(julianday(NEW.c)) IS NOT substr(NEW.c, 1, 10)
--     **先頭 10 文字が、その値が指す日そのものであること。** 「読めない」「ISO で書かれていない」
--     「実在しない日」を同時に断る。
--     - IS NOT を使う（<> だと NULL のとき比較ごと NULL になり、WHERE が偽になって黙って通す）
--     - NULL は両辺 NULL になるので通る。列が NULL 可であることを壊さない
--
--     **julianday() で包むのは、実行時の SQLite が date() で正規化しないからである。**
--     この版差は実測で見つけた（2026-09-14）——
--       date('2026-02-30')            3.41.2 は '2026-02-30' のまま / 3.53.1 は '2026-03-02'
--       date(julianday('2026-02-30')) どちらも '2026-03-02'
--     **アプリが使うのは 3.41.2**（Microsoft.Data.Sqlite 8.0.8 が抱える SQLitePCLRaw）。
--     **date() だけで書くと、実在しない日（2 月 30 日・閏年でない 2 月 29 日・4 月 31 日）が
--     取引年月日として帳簿に残る。**
--
--   3 つの GLOB（日付だけ／秒まで／秒未満つき）
--     **形そのものを見る。上の 1 本だけでは足りない**——SQLite の日付の読み取りは
--     10 文字を読んだあと空白・タブ・改行・T を読み飛ばすので、`'2026-04-01 '` は
--     両辺が一致して素通りする（2026-09-14 実測）。
--
--     **受理するのは BusinessApp.ServerSupport の DbValue が読める 5 書式ちょうど**である。
--       yyyy-MM-dd / yyyy-MM-dd HH:mm:ss[.fffffff] / yyyy-MM-ddTHH:mm:ss[.fffffff]
--     **ここを広くすると、読んだ瞬間に落ちる行が作れる**——DbValue.ParseDateTime は
--     TryParseExact なので、'…Z'・'…+09:00'・'HH:mm' 止まりは読めずに例外を投げる。
--     その 1 行が入ると AccountingMasterLoader が計上のたびに落ち、**計上が全面停止する**
--     （2026-09-14 の自己レビューで指摘。qa/02 のラウンド 89）。
--     **両側が一致していることは DateFormatGuardTests が毎回突き合わせる**
--     （008 の「トリガが通す字は関門が通す字と過不足なく一致する」と同じ形）。
--     - 秒未満は数字だけ・7 桁まで（DbValue の FFFFFFF に合わせる）
--     - **末尾に余分な字を許さない**。CSV 取込の行末の CR で穴が開く
--
--   LENGTH(CAST(NEW.c AS BLOB)) <> LENGTH(NEW.c)
--     **値の途中に U+0000 があると、上をすり抜ける**（date() も substr() も NUL で止まる）。
--     BLOB も同じ device で断る（010 が自然キーへ当てたのと同じ穴）。
--
-- **壊れた行が既に入っていたときの直し方。** `BEFORE UPDATE OF <列>` は起動条件でしかなく、
-- **本文はその表の日付の列を全部見る**。だから 1 列だけ直そうとすると、まだ壊れている
-- もう 1 列の名前で断られる。**同じ表の壊れた列は 1 つの UPDATE でまとめて直す。**
--
-- **WHEN 節を置かない。** 条件に副問い合わせが無く、置いても速くならないうえ、
-- WHEN と本文が食い違うと「WHEN で入ったのにどの RAISE にも当たらない」という
-- いちばん静かな壊れ方をする（008 はそれを MasterCodeGuardTests でわざわざ見張っている）。
-- 006 の no_overlap トリガも WHEN を持たない。
--
-- **文言は「だ・である」調で、欄の呼び名は画面のラベルを鉤括弧で括る**（docs/12 §2-1・21 §2-6）。
-- **例に挙げる形は ISO（ハイフン）である**——画面の日付は yyyy/MM/dd だが（21 §2-5）、
-- この RAISE が届くのは取込・sql CLI・SQL の直打ちで**機械に渡す値を書いている人**であり、
-- そこは 20 §6 の「機械に渡す文字列」の側である。'2026/04/01' はこのトリガが断る値でもある。
--
-- **守りは 2 段である**（12 §2-1 と同じ形）——画面は CLB の日付欄が日付しか作らせない。
-- ここは取込・CLI・SQL の直打ちに当てる最後の守りである。
--
-- **`DATE` で宣言した列は 1 つ残らず当てる**（5 表 12 列）。
-- 当初は「索引にも CHECK にも使っていない列は効かない」として 3 列を外していたが、
-- **外した列は、使い始めた日に黙って穴になる**（2026-09-14 の自己レビュー。qa/02 のラウンド 89）。
-- **列が増えたら `DateFormatGuardTests` が鳴る**——`pragma_table_info` から数えているので、
-- ここに足し忘れると赤くなる。
--
-- **当てていないのは `DATETIME` の列だけ**（created_at・updated_at・entered_at・posted_at）。
-- **利用者が書く欄ではなく、サーバが値を決める。** 要ると判断した回に同じ形で足す。

CREATE TRIGGER trg_fiscal_years_date_format_insert
BEFORE INSERT ON fiscal_years
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
    SELECT RAISE(ABORT, '「優良な電子帳簿の適用開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.premium_ledger_from)) IS NOT substr(NEW.premium_ledger_from, 1, 10)
        OR (NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.premium_ledger_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.premium_ledger_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.premium_ledger_from) <= 27))
        OR LENGTH(CAST(NEW.premium_ledger_from AS BLOB)) <> LENGTH(NEW.premium_ledger_from);
END;

CREATE TRIGGER trg_fiscal_years_date_format_update
BEFORE UPDATE OF start_date, end_date, premium_ledger_from ON fiscal_years
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
    SELECT RAISE(ABORT, '「優良な電子帳簿の適用開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.premium_ledger_from)) IS NOT substr(NEW.premium_ledger_from, 1, 10)
        OR (NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.premium_ledger_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.premium_ledger_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.premium_ledger_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.premium_ledger_from) <= 27))
        OR LENGTH(CAST(NEW.premium_ledger_from AS BLOB)) <> LENGTH(NEW.premium_ledger_from);
END;

CREATE TRIGGER trg_accounting_periods_date_format_insert
BEFORE INSERT ON accounting_periods
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
END;

CREATE TRIGGER trg_accounting_periods_date_format_update
BEFORE UPDATE OF start_date, end_date ON accounting_periods
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「開始日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.start_date)) IS NOT substr(NEW.start_date, 1, 10)
        OR (NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.start_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.start_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.start_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.start_date) <= 27))
        OR LENGTH(CAST(NEW.start_date AS BLOB)) <> LENGTH(NEW.start_date);
    SELECT RAISE(ABORT, '「終了日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.end_date)) IS NOT substr(NEW.end_date, 1, 10)
        OR (NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.end_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.end_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.end_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.end_date) <= 27))
        OR LENGTH(CAST(NEW.end_date AS BLOB)) <> LENGTH(NEW.end_date);
END;

CREATE TRIGGER trg_journal_entries_date_format_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「取引日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.transaction_date)) IS NOT substr(NEW.transaction_date, 1, 10)
        OR (NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.transaction_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.transaction_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.transaction_date) <= 27))
        OR LENGTH(CAST(NEW.transaction_date AS BLOB)) <> LENGTH(NEW.transaction_date);
    SELECT RAISE(ABORT, '「計上日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.posting_date)) IS NOT substr(NEW.posting_date, 1, 10)
        OR (NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.posting_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.posting_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.posting_date) <= 27))
        OR LENGTH(CAST(NEW.posting_date AS BLOB)) <> LENGTH(NEW.posting_date);
END;

CREATE TRIGGER trg_journal_entries_date_format_update
BEFORE UPDATE OF transaction_date, posting_date ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「取引日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.transaction_date)) IS NOT substr(NEW.transaction_date, 1, 10)
        OR (NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.transaction_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.transaction_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.transaction_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.transaction_date) <= 27))
        OR LENGTH(CAST(NEW.transaction_date AS BLOB)) <> LENGTH(NEW.transaction_date);
    SELECT RAISE(ABORT, '「計上日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.posting_date)) IS NOT substr(NEW.posting_date, 1, 10)
        OR (NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.posting_date NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.posting_date GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.posting_date, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.posting_date) <= 27))
        OR LENGTH(CAST(NEW.posting_date AS BLOB)) <> LENGTH(NEW.posting_date);
END;

-- 明細の「課税仕入れの時点」。**いまは画面が書かないが、列はある**——
-- フェーズ 3 で経過措置の判定に使う（docs/11 §5）。**使い始める前に塞いでおく。**

CREATE TRIGGER trg_journal_lines_date_format_insert
BEFORE INSERT ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「課税仕入れの時点」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.tax_point)) IS NOT substr(NEW.tax_point, 1, 10)
        OR (NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.tax_point GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.tax_point, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.tax_point) <= 27))
        OR LENGTH(CAST(NEW.tax_point AS BLOB)) <> LENGTH(NEW.tax_point);
END;

CREATE TRIGGER trg_journal_lines_date_format_update
BEFORE UPDATE OF tax_point ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「課税仕入れの時点」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.tax_point)) IS NOT substr(NEW.tax_point, 1, 10)
        OR (NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.tax_point NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.tax_point GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.tax_point, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.tax_point) <= 27))
        OR LENGTH(CAST(NEW.tax_point AS BLOB)) <> LENGTH(NEW.tax_point);
END;

CREATE TRIGGER trg_partner_invoice_registrations_date_format_insert
BEFORE INSERT ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「登録年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR (NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_from) <= 27))
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「取消・失効年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.ended_on)) IS NOT substr(NEW.ended_on, 1, 10)
        OR (NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.ended_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.ended_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.ended_on) <= 27))
        OR LENGTH(CAST(NEW.ended_on AS BLOB)) <> LENGTH(NEW.ended_on);
    SELECT RAISE(ABORT, '「最終確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR (NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.confirmed_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.confirmed_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.confirmed_on) <= 27))
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
    SELECT RAISE(ABORT, '「公表システムの更新年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.nta_updated_on)) IS NOT substr(NEW.nta_updated_on, 1, 10)
        OR (NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.nta_updated_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.nta_updated_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.nta_updated_on) <= 27))
        OR LENGTH(CAST(NEW.nta_updated_on AS BLOB)) <> LENGTH(NEW.nta_updated_on);
END;

CREATE TRIGGER trg_partner_invoice_registrations_date_format_update
BEFORE UPDATE OF valid_from, ended_on, confirmed_on, nta_updated_on ON partner_invoice_registrations
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '「登録年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.valid_from)) IS NOT substr(NEW.valid_from, 1, 10)
        OR (NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.valid_from NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.valid_from GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.valid_from, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.valid_from) <= 27))
        OR LENGTH(CAST(NEW.valid_from AS BLOB)) <> LENGTH(NEW.valid_from);
    SELECT RAISE(ABORT, '「取消・失効年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.ended_on)) IS NOT substr(NEW.ended_on, 1, 10)
        OR (NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.ended_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.ended_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.ended_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.ended_on) <= 27))
        OR LENGTH(CAST(NEW.ended_on AS BLOB)) <> LENGTH(NEW.ended_on);
    SELECT RAISE(ABORT, '「最終確認日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.confirmed_on)) IS NOT substr(NEW.confirmed_on, 1, 10)
        OR (NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.confirmed_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.confirmed_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.confirmed_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.confirmed_on) <= 27))
        OR LENGTH(CAST(NEW.confirmed_on AS BLOB)) <> LENGTH(NEW.confirmed_on);
    SELECT RAISE(ABORT, '「公表システムの更新年月日」は実在する年月日を「2026-04-01」の形で入れる。')
     WHERE date(julianday(NEW.nta_updated_on)) IS NOT substr(NEW.nta_updated_on, 1, 10)
        OR (NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'
            AND NEW.nta_updated_on NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'
            AND NOT (NEW.nta_updated_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9]*'
                     AND substr(NEW.nta_updated_on, 21) NOT GLOB '*[^0-9]*'
                     AND LENGTH(NEW.nta_updated_on) <= 27))
        OR LENGTH(CAST(NEW.nta_updated_on AS BLOB)) <> LENGTH(NEW.nta_updated_on);
END;
