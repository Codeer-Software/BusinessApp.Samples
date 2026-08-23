-- 004 勘定科目・補助科目・部門・取引先（ADR-0006 ③ 会計マスタ）
--
-- 削除ではなく無効化する。is_active は「入力時の候補に出すか」の意味であり、
-- 過去データの表示・検索は妨げない（ADR-0006）。論理削除の列は置かない。

CREATE TABLE accounts (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    code                        TEXT NOT NULL UNIQUE,       -- 4 桁。資産 1xxx / 負債 2xxx / 純資産 3xxx / 収益 4xxx / 費用 5xxx〜
    name                        TEXT NOT NULL,
    name_kana                   TEXT,

    -- 科目区分。部門の要否判定（I-13）と決算振替がこれに依存するので NOT NULL。
    category                    TEXT NOT NULL CHECK (category IN (
                                    'asset', 'liability', 'equity', 'revenue', 'expense')),

    -- 決算書表示区分。科目区分とは別物で、「財務諸表のどこに並べるか」だけを表す（docs/04 §6）。
    statement_section           TEXT,

    -- 入力時の初期値。**値が入っていない行の穴埋めに使わない**（前回プロジェクトの実測で事故った）。
    default_tax_category_id     INTEGER REFERENCES tax_categories(id),

    requires_sub_account        INTEGER NOT NULL DEFAULT 0 CHECK (requires_sub_account IN (0, 1)),
    is_cash_equivalent          INTEGER NOT NULL DEFAULT 0 CHECK (is_cash_equivalent IN (0, 1)),
    is_fixed_asset              INTEGER NOT NULL DEFAULT 0 CHECK (is_fixed_asset IN (0, 1)),
    is_active                   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    display_order               INTEGER,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0006）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE sub_accounts (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    account_id                  INTEGER NOT NULL REFERENCES accounts(id),
    code                        TEXT NOT NULL,
    name                        TEXT NOT NULL,
    name_kana                   TEXT,
    is_active                   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    display_order               INTEGER,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0006）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    UNIQUE (account_id, code)
);

-- 部門は単一階層（ADR-0010）。親部門の列を持たないことが、その決定の実装である。
CREATE TABLE departments (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    code                        TEXT NOT NULL UNIQUE,
    name                        TEXT NOT NULL,

    -- 「全社共通」。利用者が意図して選ぶときだけ使う枠であり、
    -- 空欄の穴埋めには使わない（docs/04 §9-1）。
    is_company_wide             INTEGER NOT NULL DEFAULT 0 CHECK (is_company_wide IN (0, 1)),

    is_active                   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    display_order               INTEGER,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0006）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0
);

-- 「全社共通」は 1 件だけ。部分 UNIQUE インデックスで DB に守らせる。
CREATE UNIQUE INDEX ux_departments_company_wide
    ON departments (is_company_wide) WHERE is_company_wide = 1;

CREATE TABLE partners (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    code                        TEXT NOT NULL UNIQUE,
    name                        TEXT NOT NULL,
    name_kana                   TEXT,
    is_active                   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    display_order               INTEGER,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0006）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0
);

-- 適格請求書発行事業者としての登録状況はここに列で持たない。
-- 登録・取消には日付があり、1 列では「いつ時点で登録事業者だったか」を表せない（docs/06 §1）。
-- 有効期間つきの partner_invoice_registrations をフェーズ 3 で追加する。
