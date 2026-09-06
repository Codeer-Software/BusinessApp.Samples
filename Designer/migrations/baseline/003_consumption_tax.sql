-- 003 税区分（ADR-0006 ③ 会計マスタ）
--
-- 1 つの「税区分」に複数の軸を押し込まない（docs/11 §1）。ここが持つのは課税区分だけで、
--   税率        → 制度ルール（有効期間つき）。フェーズ 3 で別テーブルにする
--   用途区分    → 仕訳明細（journal_lines.tax_treatment）。同じ科目でも取引ごとに変わる
--   登録状況    → 取引先（有効期間つき）。フェーズ 3
-- をここに混ぜない。
--
-- 税率をこの表に持たせないのは、税率が「税区分の属性」ではなく「ある期間に有効な制度値」
-- だからである（docs/20_実装の原則.md §2）。列にすると改正のたびに過去が書き換わる。

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
    --   legacy_8  旧税率 8%（経過措置等）
    --   NULL      税率の概念がない区分（非課税・免税・対象外）
    rate_kind                   TEXT CHECK (rate_kind IN ('standard', 'reduced', 'legacy_8')),

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
    -- ユーザーは会計コアの責務ではなく別部品のものなので（ADR-0006）、DB 制約で結ぶと
    -- 会計コアが認証部品なしでは立ち上がらなくなる。CLB の予約名として値は自動で入る。
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 課税区分と税率区分の整合。課税でない区分に税率区分は付かず、課税の区分には必ず付く。
    CHECK (taxation_type IN ('taxable_sales', 'taxable_purchase') OR rate_kind IS NULL),
    CHECK (taxation_type NOT IN ('taxable_sales', 'taxable_purchase') OR rate_kind IS NOT NULL)
);
