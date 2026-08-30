-- 004 勘定科目・補助科目・部門・取引先（docs/08_マスタ台帳）
--
-- 削除ではなく無効化する。is_active は「入力時の候補に出すか」の意味であり、
-- 過去データの表示・検索は妨げない（docs/08_マスタ台帳）。論理削除の列は置かない。

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

    -- 評価勘定（控除科目）か。減価償却累計額・売上値引戻り高・期末仕掛品棚卸高のように、
    -- **通常残高が科目区分と逆**の科目がある。この列が無いと、科目区分だけから
    -- 借方残／貸方残を決める処理が必ず誤る（試算表の異常値判定・決算書の控除表示）。
    is_contra                   INTEGER NOT NULL DEFAULT 0 CHECK (is_contra IN (0, 1)),

    requires_sub_account        INTEGER NOT NULL DEFAULT 0 CHECK (requires_sub_account IN (0, 1)),
    is_cash_equivalent          INTEGER NOT NULL DEFAULT 0 CHECK (is_cash_equivalent IN (0, 1)),
    is_fixed_asset              INTEGER NOT NULL DEFAULT 0 CHECK (is_fixed_asset IN (0, 1)),
    is_active                   INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    display_order               INTEGER,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0029）、DB 制約で結ぶと
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
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0029）、DB 制約で結ぶと
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
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0029）、DB 制約で結ぶと
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
    -- **display_order は持たない**（2026-08-31 に落とした。docs/07 §1-2）。
    -- マスタのテンプレートから写しただけで、どの行にも値が入っていなかった。
    -- 取引先は数百件に育つもので、人が並び順を手で維持することはあり得ない。

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0029）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 取引先の素性（docs/07 §1-2）。新しい列は末尾に置く（migrations/README の規約）。
    -- 所在地は 1 列で持つ。都道府県・市区町村に分割しない。
    address                     TEXT,
    -- 種別。法人（設立登記法人）／個人事業者／人格のない社団等／その他。
    -- **画面では必須、DB は NULL 可。** 既存の行と「まだ分類していない」を
    -- 偽の値で埋めないため（posted_by と同じ規律）。NULL は「未分類」を表す。
    entity_type                 TEXT CHECK (entity_type IN ('corporation', 'sole_proprietor', 'unincorporated_association', 'other')),
    -- 法人番号（13 桁）。任意（docs/07 §2-3。必須にすると迂回のダミー値が入る）。
    -- 検査数字の検証はアプリ側。DB は桁と数字だけを見る。
    corporate_number            TEXT CHECK (corporate_number IS NULL OR corporate_number GLOB '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]'),
    -- 名寄せの手動キー（docs/07 §2-2）。自分自身は束ねられない。
    -- 2 段より深い連鎖（A→B→C）や循環（A→B→A）は行をまたぐので DB では見ない。
    -- 解決の順序（法人番号 → 親 → 自分）はアプリ側が持つ。
    parent_partner_id           INTEGER REFERENCES partners(id) CHECK (parent_partner_id IS NULL OR parent_partner_id <> id),

    -- 個人事業者に法人番号は指定されない（法人番号の指定対象の列挙に個人事業者が無い。
    -- docs/research の取引先リサーチ §1 からの導出）。
    -- 矛盾した行を許すと、法人番号が自然キーとして名寄せされ、誤束ねが黙って起こる。
    CHECK (entity_type IS NULL OR entity_type <> 'sole_proprietor' OR corporate_number IS NULL)
);

-- 名寄せの親は**深さ 1 の森**に固定する（ADR-0028 §2）。行をまたぐ規則なので CHECK では書けない。
-- **アプリ側の関門（PartnerSubmitGate）が同じ規則を先に見る。** ここは最後の砦である
-- （ADR-0004 の「最後に守るのは DB」と同じ形）——取込・API・SQL の直打ちも通る。
--
-- **この 2 つで循環は構造的に消える。** 深さ 2 以上が作れなければ、A→B→A も作れない。
-- 自己参照（A→A）は列の CHECK が拒む。
--
-- **INSERT では「自分が既に親になっている」を見ない。** BEFORE INSERT の時点で NEW.id は
-- まだ採番されておらず、そもそも生まれたばかりの行を親にしている行は無い。
CREATE TRIGGER trg_partners_parent_must_be_root_insert
BEFORE INSERT ON partners
WHEN NEW.parent_partner_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, '名寄せの親には、さらに親を持つ取引先を選べません。')
     WHERE EXISTS (SELECT 1 FROM partners p
                    WHERE p.id = NEW.parent_partner_id AND p.parent_partner_id IS NOT NULL);
END;

CREATE TRIGGER trg_partners_parent_must_be_root_update
BEFORE UPDATE OF parent_partner_id ON partners
WHEN NEW.parent_partner_id IS NOT NULL
BEGIN
    SELECT RAISE(ABORT, '名寄せの親には、さらに親を持つ取引先を選べません。')
     WHERE EXISTS (SELECT 1 FROM partners p
                    WHERE p.id = NEW.parent_partner_id AND p.parent_partner_id IS NOT NULL);
    SELECT RAISE(ABORT, '他の取引先の名寄せの親になっている取引先には、親を付けられません。')
     WHERE EXISTS (SELECT 1 FROM partners c WHERE c.parent_partner_id = NEW.id);
END;

-- 適格請求書発行事業者としての登録状況はここに列で持たない。
-- 登録・取消には日付があり、1 列では「いつ時点で登録事業者だったか」を表せない（docs/07 §3-1）。
-- 有効期間つきの partner_invoice_registrations は 006_partner_registrations.sql が持つ。
