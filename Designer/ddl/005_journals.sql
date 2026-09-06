-- 005 仕訳・仕訳明細・伝票番号の採番
--
-- 日付を 1 つに潰さない（docs/10 §2）。取引日・計上日・入力年月日・課税仕入れの時点は
-- それぞれ意味が違い、共用すると必ずどこかで壊れる。とくに入力年月日は
-- 「通常の業務処理期間の経過後の入力の事実を確認できる」という優良な電子帳簿の要件
-- （電帳規則 5 ⑤一イ(2)）そのものなので、取引日と絶対に共用しない。

CREATE TABLE journal_entries (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    -- 伝票番号。計上時に採番する。下書きのうちは NULL。
    -- 会計年度ごとの連番（欠番を埋め直さず、再利用しない。I-17）。
    fiscal_year_id              INTEGER NOT NULL REFERENCES fiscal_years(id),
    entry_no                    INTEGER,

    transaction_date            DATE NOT NULL,              -- 取引日。帳簿の「取引年月日」（法定記載事項②）
    posting_date                DATE NOT NULL,              -- 計上日。会計期間への帰属を決める（I-03）

    status                      TEXT NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted')),
    entry_type                  TEXT NOT NULL DEFAULT 'normal' CHECK (entry_type IN (
                                    'normal', 'correction', 'reversal',
                                    'opening', 'closing', 'carryover')),

    original_entry_id           INTEGER REFERENCES journal_entries(id),
    description                 TEXT,
    partner_id                  INTEGER REFERENCES partners(id),

    -- 他部品からの投入（docs/10 §10）。手入力は NULL。
    source_component            TEXT,
    source_document_id          TEXT,
    idempotency_key             TEXT UNIQUE,                -- 同一の外部伝票を二重に計上しない（I-14）

    -- システムが付ける。利用者は変更できない。
    entered_at                  DATETIME NOT NULL,
    posted_at                   DATETIME,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0029）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 計上した人（認証部品のユーザー識別子。creator と同じ理由で外部キーを張らない）。
    -- 計上時にサーバが書く。下書きは NULL。この列より前に計上された伝票も NULL のまま。
    -- 新しい列は末尾（テーブル制約の前）に置く規約（migrations/README。ADD COLUMN と同値になる位置）。
    posted_by                   INTEGER,

    -- 伝票の取引先の名前の写し。計上時にサーバが焼く（ADR-0018・ADR-0037）。
    -- **明細の写し（journal_lines.partner_name_snapshot）とは役割が違う。**
    -- あちらは帳簿の法定記載事項（消税法 30 ⑧）で、こちらは**伝票の画面が計上時の姿を見せる**ためのもの。
    -- 計上済みの伝票は不変（I-05）なのに、参照先のマスタを改名すると画面の表示だけが動く——
    -- それを止める。**反対仕訳は原仕訳の値を引き継ぐ**（焼き直すと表と裏で名前が変わる）。
    -- この列より前に計上された伝票は NULL のまま。取引先の無い伝票も NULL。
    partner_name_snapshot       TEXT,

    -- I-06 訂正・取消は原仕訳を一意に特定する情報を持つ
    CHECK (entry_type NOT IN ('correction', 'reversal') OR original_entry_id IS NOT NULL),
    -- 自分自身を原仕訳にできない（自分を取り消す伝票は意味を成さない）
    CHECK (original_entry_id IS NULL OR original_entry_id <> id),
    -- 計上済みには必ず伝票番号と計上日時がある
    CHECK (status = 'draft' OR (entry_no IS NOT NULL AND posted_at IS NOT NULL)),
    -- 下書きに伝票番号を与えない（番号の先食いを防ぐ）
    CHECK (status = 'posted' OR entry_no IS NULL),
    -- 伝票番号は整数の連番である（金額と同じ理由で typeof を要求する）
    CHECK (entry_no IS NULL OR typeof(entry_no) = 'integer'),
    -- I-17 会計年度の中で伝票番号は一意
    UNIQUE (fiscal_year_id, entry_no)
);

CREATE INDEX ix_journal_entries_posting_date ON journal_entries (posting_date);
CREATE INDEX ix_journal_entries_transaction_date ON journal_entries (transaction_date);
CREATE INDEX ix_journal_entries_original ON journal_entries (original_entry_id);

-- 1 本の仕訳を取り消す反対仕訳は 1 本まで（I-05 の系）。
-- **二重取消は残高を狂わせる。** 反対仕訳が 2 本残っても、元の取引は 1 回しか無い。
-- アプリ側も計上前に検査するが、同時に 2 人が取り消すと両方が「まだ取り消されていない」を
-- 読んでしまう。最後に止めるのは DB である。
CREATE UNIQUE INDEX ux_journal_entries_single_reversal
    ON journal_entries (original_entry_id)
    WHERE entry_type = 'reversal' AND status = 'posted';

-- 1 本の仕訳を訂正する再計上も 1 本まで（ADR-0015）。
-- **再計上が 2 本載ると、直した内容がそのまま二重に計上される。**
-- 訂正をやり直したいときは、その訂正の伝票を訂正する（訂正は訂正できる）。
-- 取消と同じく、アプリ側の検査は同時実行に勝てないので最後は DB が止める。
CREATE UNIQUE INDEX ux_journal_entries_single_correction
    ON journal_entries (original_entry_id)
    WHERE entry_type = 'correction' AND status = 'posted';

CREATE TABLE journal_lines (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    journal_entry_id            INTEGER NOT NULL REFERENCES journal_entries(id),
    line_no                     INTEGER NOT NULL CHECK (line_no > 0 AND typeof(line_no) = 'integer'),

    -- 借方貸方は符号ではなく区分で持ち、金額は常に正（docs/10 §3）。
    debit_credit                TEXT NOT NULL CHECK (debit_credit IN ('debit', 'credit')),

    account_id                  INTEGER NOT NULL REFERENCES accounts(id),
    sub_account_id              INTEGER REFERENCES sub_accounts(id),
    department_id               INTEGER REFERENCES departments(id),   -- 損益科目では必須（I-13。判定は科目区分が要るのでアプリ側）

    partner_id                  INTEGER REFERENCES partners(id),
    -- 取引先名の写し。帳簿の法定記載事項①（消税法 30 ⑧）であり、
    -- 取引先の改名で過去の帳簿の記載が変わらないように FK と両方持つ（docs/10 §4-2）。
    partner_name_snapshot       TEXT,

    -- 税抜・正の整数円。REAL を使わない。
    -- **INTEGER と書くだけでは整数にならない。** SQLite の型親和性は 100.5 を整数に落とせず、
    -- REAL のまま格納する。typeof で明示的に拒まないと、貸借一致の判定と保存値がずれる（I-01）。
    amount                      INTEGER NOT NULL CHECK (amount > 0 AND typeof(amount) = 'integer'),

    -- 税に意味のない行にも「対象外」を明示する。NULL と対象外を 2 通りで表さない（docs/11 §1）。
    tax_category_id             INTEGER NOT NULL REFERENCES tax_categories(id),
    -- 用途区分は明細が持つ。同じ科目でも取引ごとに変わるため。
    tax_treatment               TEXT CHECK (tax_treatment IN ('for_taxable_sales', 'common', 'for_exempt_sales')),
    tax_point                   DATE,                       -- 課税仕入れの時点。経過措置・税率の判定基準日
    applied_rule_version        TEXT,                       -- 適用した制度ルールの版（I-16）

    is_tax_line                 INTEGER NOT NULL DEFAULT 0 CHECK (is_tax_line IN (0, 1)),
    parent_line_no              INTEGER,                    -- 消費税行が対応する本体行

    item_description            TEXT,                       -- 資産又は役務の内容（法定記載事項③）
    book_only_deduction         TEXT,                       -- 帳簿のみ保存で控除する類型
    evidence_ref                TEXT,                       -- 証憑部品への参照キー

    -- 計上時点の登録番号の写し（docs/13 §4・ADR-0018）。
    -- **帳簿の法定記載事項ではない**（消税法 30 ⑧に相手方の登録番号は含まれない）。
    -- 持つ理由は「判定の根拠の記録」——CSV の入出力と監査追跡が当時の値を要求するため。
    -- 現在のマスタから引くと、登録を取り消した相手で当時と違う値が出る。
    -- 帳簿には印字しない。**この行の「取引先」の番号であって、自社の番号ではない**
    -- （適格請求書に載る登録番号は売手＝自社のもの。消税法 57 の 4 ①）。
    -- 引く日付は tax_point（課税仕入れを行った日）。**tax_point が空なら伝票の取引日で引く**
    -- （docs/13 §4-1。画面が tax_point を入力させないので、無ければ写さないにすると永久に空になる）。
    registration_no_snapshot    TEXT,

    UNIQUE (journal_entry_id, line_no),
    -- 消費税行だけが親行を持ち、消費税行は必ず親行を持つ
    CHECK (is_tax_line = 1 OR parent_line_no IS NULL),
    CHECK (is_tax_line = 0 OR parent_line_no IS NOT NULL)
);

CREATE INDEX ix_journal_lines_entry ON journal_lines (journal_entry_id);
CREATE INDEX ix_journal_lines_account ON journal_lines (account_id);
CREATE INDEX ix_journal_lines_department ON journal_lines (department_id);
CREATE INDEX ix_journal_lines_partner ON journal_lines (partner_id);

-- 伝票番号の採番（I-17）。
-- 会計年度ごとに 1 行。next_entry_no は増やすだけで、戻さない・埋め直さない。
-- 下書きを消しても番号は消費されない（番号は計上時にしか採らないため）。
CREATE TABLE journal_entry_sequences (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    fiscal_year_id              INTEGER NOT NULL UNIQUE REFERENCES fiscal_years(id),
    next_entry_no               INTEGER NOT NULL DEFAULT 1 CHECK (next_entry_no >= 1)
);

--------------------------------------------------------------------------------
-- I-05 計上済み仕訳は変更も削除もされない
--
-- AccountingCore の検証とサーバの関門が第一の防波堤だが、CSV 取込・API・スクリプト・
-- 手作業の SQL のどれからでも通る最後の関門として、DB にも置く。
-- 「規則を迂回する経路を作らない」（ADR-0004）を規約ではなく DB に守らせる。
--
-- 下書き → 計上（status が draft から posted へ変わる UPDATE）は通す。
-- 計上済みの行に対する UPDATE / DELETE だけを止める。
--------------------------------------------------------------------------------

-- 計上は「下書きとして書いてから status を進める」経路しか無い。
-- **最初から計上済みとして INSERT する道を塞ぐ。** ここが開いていると、
-- 貸借不一致・明細ゼロの計上済み伝票を直接書き込めてしまい、しかも他のトリガが
-- UPDATE も DELETE も明細の追加も止めるので、**訂正も取消もできない行が恒久的に残る**。
CREATE TRIGGER trg_journal_entries_no_posted_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW WHEN NEW.status = 'posted'
BEGIN
    SELECT RAISE(ABORT, '仕訳は下書きとして作る。計上は検証を通してから状態を進める。');
END;

-- 入力年月日は「システムに記録された日時」であり、**下書きの間も含めて後から変えられない**。
-- 「通常の業務処理期間の経過後に入力した事実を確認できる」という優良な電子帳簿の要件
-- （電帳規則 5 ⑤一イ(2)）は、この値が動かないことで初めて成り立つ。
CREATE TRIGGER trg_journal_entries_entered_at_immutable
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.entered_at IS NOT OLD.entered_at
BEGIN
    SELECT RAISE(ABORT, '入力年月日は変更できない。');
END;

CREATE TRIGGER trg_journal_entries_posted_no_update
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN OLD.status = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳は変更できない。訂正・取消は反対仕訳で行う。');
END;

-- 種別は伝票を作るときに決まるもので、**後から変えてよいものではない**。
--
-- 変えられると、種別ごとの関門（JournalSubmitGate のホワイトリスト）が丸ごと外れる。
-- とくに「訂正」を「通常」に変えると、原仕訳との関係を見る検証を通らないまま計上でき、
-- しかも二重訂正の検出（下の部分ユニークインデックスと HasCorrectionAsync）は
-- entry_type = 'correction' の行しか数えないので、**同じ原仕訳にもう 1 本訂正を計上できる**。
-- 取引が帳簿に 2 回載る（2026-08-25 の自己レビューで発見）。
--
-- 直したいなら下書きを作り直す。下書きは自由に消せる。
CREATE TRIGGER trg_journal_entries_entry_type_immutable
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.entry_type IS NOT OLD.entry_type
BEGIN
    SELECT RAISE(ABORT, '仕訳の種別は変更できない。種別を変えるなら下書きを作り直す。');
END;

-- 原仕訳を指してよいのは訂正・取消だけ（I-06 の逆向き）。
--
-- 通常の仕訳が原仕訳を指していると、「訂正のつもりで作った通常の仕訳」が
-- 帳簿の上では原仕訳と無関係の新しい取引として振る舞う。CHECK で書きたいところだが、
-- 既存のテーブルに CHECK を足すには作り直しが要るので、同じ規則をトリガで置く。
CREATE TRIGGER trg_journal_entries_original_only_for_amendment
BEFORE INSERT ON journal_entries
FOR EACH ROW WHEN NEW.original_entry_id IS NOT NULL
                  AND NEW.entry_type NOT IN ('correction', 'reversal')
BEGIN
    SELECT RAISE(ABORT, '原仕訳を指定できるのは訂正・取消だけ。');
END;

CREATE TRIGGER trg_journal_entries_original_only_for_amendment_update
BEFORE UPDATE ON journal_entries
FOR EACH ROW WHEN NEW.original_entry_id IS NOT NULL
                  AND NEW.entry_type NOT IN ('correction', 'reversal')
BEGIN
    SELECT RAISE(ABORT, '原仕訳を指定できるのは訂正・取消だけ。');
END;

CREATE TRIGGER trg_journal_entries_posted_no_delete
BEFORE DELETE ON journal_entries
FOR EACH ROW WHEN OLD.status = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳は削除できない。訂正・取消は反対仕訳で行う。');
END;

CREATE TRIGGER trg_journal_lines_posted_no_update
BEFORE UPDATE ON journal_lines
FOR EACH ROW WHEN (SELECT status FROM journal_entries WHERE id = OLD.journal_entry_id) = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細は変更できない。');
END;

CREATE TRIGGER trg_journal_lines_posted_no_delete
BEFORE DELETE ON journal_lines
FOR EACH ROW WHEN (SELECT status FROM journal_entries WHERE id = OLD.journal_entry_id) = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細は削除できない。');
END;

-- **明細を計上済みの伝票へ「付け替える」道を塞ぐ。**
--
-- 上の 2 本は OLD の伝票（＝いま所属している伝票）の状態しか見ないので、
-- **下書きの明細の journal_entry_id を計上済みの伝票の id へ UPDATE する**経路は
-- どれにも当たらず通っていた（2026-09-03 の自己レビューで発見。qa/03 L-25）。
-- 通ると、計上済みの伝票に身に覚えのない行が増える——貸借一致（I-01）も
-- 不変性（I-05）もその瞬間に破れ、しかも訂正も取消もできない。
CREATE TRIGGER trg_journal_lines_no_move_into_posted
BEFORE UPDATE ON journal_lines
FOR EACH ROW WHEN (SELECT status FROM journal_entries WHERE id = NEW.journal_entry_id) = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳へ明細を移動できない。');
END;

CREATE TRIGGER trg_journal_lines_posted_no_insert
BEFORE INSERT ON journal_lines
FOR EACH ROW WHEN (SELECT status FROM journal_entries WHERE id = NEW.journal_entry_id) = 'posted'
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳に明細を追加できない。');
END;

--------------------------------------------------------------------------------
-- REPLACE の暗黙の DELETE を塞ぐ
--
-- **SQLite は、REPLACE が制約充足のために消す行の DELETE トリガを、
-- `PRAGMA recursive_triggers` が OFF のあいだ発火しない**（既定は OFF）。
-- そのため `INSERT OR REPLACE INTO journal_entries (id, ..., 'draft', ...)` の 1 文で、
-- **計上済みの伝票が音もなく下書きに戻る**（2026-09-03 に再現。qa/03 L-26）。
-- 明細も同じで、`id` をぶつけると計上済みの明細が 1 行黙って消える（I-01 が同時に破れる）。
--
-- **接続の PRAGMA に頼らない。** ここは「手作業の SQL からでも通る最後の関門」であり、
-- 接続ごとの設定は、まさにその手作業の経路で外れる。**衝突そのものを拒む。**
--------------------------------------------------------------------------------

CREATE TRIGGER trg_journal_entries_no_replace_posted_insert
BEFORE INSERT ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳を上書きできない。訂正・取消は反対仕訳で行う。')
     WHERE EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.status = 'posted'
                      AND (e.id = NEW.id
                           OR (NEW.idempotency_key IS NOT NULL
                               AND e.idempotency_key = NEW.idempotency_key)));
END;

CREATE TRIGGER trg_journal_entries_no_replace_posted_update
BEFORE UPDATE ON journal_entries
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳を上書きできない。訂正・取消は反対仕訳で行う。')
     WHERE EXISTS (SELECT 1 FROM journal_entries e
                    WHERE e.status = 'posted' AND e.id <> OLD.id
                      AND (e.id = NEW.id
                           OR (NEW.idempotency_key IS NOT NULL
                               AND e.idempotency_key = NEW.idempotency_key)));
END;

CREATE TRIGGER trg_journal_lines_no_replace_posted_insert
BEFORE INSERT ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.id = NEW.id AND e.status = 'posted');
END;

CREATE TRIGGER trg_journal_lines_no_replace_posted_update
BEFORE UPDATE ON journal_lines
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, '計上済みの仕訳明細を上書きできない。')
     WHERE EXISTS (SELECT 1 FROM journal_lines l
                     JOIN journal_entries e ON e.id = l.journal_entry_id
                    WHERE l.id = NEW.id AND l.id <> OLD.id AND e.status = 'posted');
END;
