-- 003 税区分（ADR-0006 ③ 会計マスタ）
--
-- 1 つの「税区分」に複数の軸を押し込まない（docs/06 §1）。ここが持つのは課税区分だけで、
--   税率        → 制度ルール（有効期間つき）。フェーズ 3 で別テーブルにする
--   用途区分    → 仕訳明細（journal_lines.tax_treatment）。同じ科目でも取引ごとに変わる
--   登録状況    → 取引先（有効期間つき）。フェーズ 3
-- をここに混ぜない。
--
-- 税率をこの表に持たせないのは、税率が「税区分の属性」ではなく「ある期間に有効な制度値」
-- だからである（CLAUDE.md §2-3）。列にすると改正のたびに過去が書き換わる。

CREATE TABLE tax_categories (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    code                        TEXT NOT NULL UNIQUE,
    name                        TEXT NOT NULL,

    -- 課税区分。docs/06 §1 の 5 値。
    --   taxable_sales    課税売上
    --   taxable_purchase 課税仕入
    --   non_taxable      非課税
    --   export_exempt    免税（輸出）
    --   out_of_scope     不課税（対象外）
    -- 「未設定」を NULL と out_of_scope の 2 通りで表さないため NOT NULL にする。
    taxation_type               TEXT NOT NULL CHECK (taxation_type IN (
                                    'taxable_sales', 'taxable_purchase',
                                    'non_taxable', 'export_exempt', 'out_of_scope')),

    -- 入力時の初期値としての用途区分。明細の値が正であり、
    -- 「値が入っていない行の穴埋め」には使わない（docs/06 §1）。
    default_tax_treatment       TEXT CHECK (default_tax_treatment IN (
                                    'taxable_sales', 'common', 'exempt_sales')),

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
