-- 取引先の「表示順」を落とし、名寄せの親を深さ 1 の森に固定する（ADR-0028）。
-- あわせて、登録が「同じ取引先・同じ日」で 2 件入らないようにする（qa/02 R26-20）。
--
-- **1 本にまとめる理由**: どちらも partners を作り直す（ADR-0028 の帰結）。
-- 列の削除とトリガの追加は、どちらも作り直しでしか行えない（migrations/README のレシピ）。
--
-- **適用前の確認**: 既存の行が新しい規則に反していると、書き戻しや索引の作成で失敗して
-- 丸ごと巻き戻る（データは失われないが、先に手で直さないと適用できない）。
-- 次の 3 つが 0 件であることを確かめること。
--   -- 親がさらに親を持つ組
--   SELECT COUNT(*) FROM partners c JOIN partners p ON p.id = c.parent_partner_id
--    WHERE p.parent_partner_id IS NOT NULL;
--   -- 自分が誰かの親なのに、自分にも親がある
--   SELECT COUNT(*) FROM partners x
--    WHERE x.parent_partner_id IS NOT NULL
--      AND EXISTS (SELECT 1 FROM partners c WHERE c.parent_partner_id = x.id);
--   -- 同じ取引先・同じ日から始まる登録
--   SELECT COUNT(*) FROM (SELECT partner_id, date(valid_from) d
--                           FROM partner_invoice_registrations
--                          GROUP BY partner_id, d HAVING COUNT(*) > 1);
--
-- **トリガは INSERT / UPDATE でしか発火しない**ので、作り直しても既存の矛盾行は残る。
-- だから上の SELECT を先に流す。

PRAGMA defer_foreign_keys = ON;

-- ① partners の作り直し（display_order を落とす）
CREATE TABLE partners_rebuild AS SELECT * FROM partners;
CREATE TABLE partners_rebuild_seq AS SELECT seq FROM sqlite_sequence WHERE name = 'partners';
DROP TABLE partners;

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

INSERT INTO partners (id, code, name, name_kana, is_active,
                      created_at, updated_at, creator, updater, optimistic_locking,
                      address, entity_type, corporate_number, parent_partner_id)
    SELECT id, code, name, name_kana, is_active,
           created_at, updated_at, creator, updater, optimistic_locking,
           address, entity_type, corporate_number, parent_partner_id
    FROM partners_rebuild;
DROP TABLE partners_rebuild;
-- 採番の復元（レシピ⑧）。書き戻しが 0 行だと sqlite_sequence に行が無いので INSERT の分岐が要る。
UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM partners_rebuild_seq))
 WHERE name = 'partners' AND EXISTS (SELECT 1 FROM partners_rebuild_seq);
INSERT INTO sqlite_sequence (name, seq)
    SELECT 'partners', seq FROM partners_rebuild_seq
    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'partners');
DROP TABLE partners_rebuild_seq;

-- ② 名寄せの深さ 1 を守るトリガ（正典からの逐語の写し）
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

-- ③ 登録は「同じ取引先・同じ日」で 1 件だけ（正典からの逐語の写し）
CREATE UNIQUE INDEX ux_partner_invoice_registrations_valid_from
    ON partner_invoice_registrations (partner_id, date(valid_from));
