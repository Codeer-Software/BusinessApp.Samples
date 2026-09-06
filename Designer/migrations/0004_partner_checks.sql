-- 取引先まわりの CHECK を 2 本足し、冗長な索引をやめる（2026-08-25 の自己レビュー指摘）。
-- ① partners: 個人事業者に法人番号は指定されない（制度事実）を DB でも拒む
-- ② partner_invoice_registrations: 登録より前に終わる行（転記ミス）を拒む。partner_id 単独索引を廃止
-- テーブル制約の追加は作り直しで行う（migrations/README のレシピ）。CREATE 文は正典からの逐語の写し。
--
-- **適用前の確認**: 既存の行が新しい CHECK に反していると、書き戻しで失敗して丸ごと巻き戻る
-- （データは失われないが、先に手で直さないと適用できない）。次の 2 つが 0 件であることを確かめること。
--   SELECT COUNT(*) FROM partners WHERE entity_type = 'sole_proprietor' AND corporate_number IS NOT NULL;
--   SELECT COUNT(*) FROM partner_invoice_registrations WHERE ended_on < valid_from;

PRAGMA defer_foreign_keys = ON;

-- ① partners の作り直し
CREATE TABLE partners_rebuild AS SELECT * FROM partners;
CREATE TABLE partners_rebuild_seq AS SELECT seq FROM sqlite_sequence WHERE name = 'partners';
DROP TABLE partners;

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
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 取引先の素性（docs/13 §1-2）。新しい列は末尾に置く（migrations/README の規約）。
    -- 所在地は 1 列で持つ。都道府県・市区町村に分割しない。
    address                     TEXT,
    -- 種別。法人（設立登記法人）／個人事業者／人格のない社団等／その他。
    -- **画面では必須、DB は NULL 可。** 既存の行と「まだ分類していない」を
    -- 偽の値で埋めないため（posted_by と同じ規律）。NULL は「未分類」を表す。
    entity_type                 TEXT CHECK (entity_type IN ('corporation', 'sole_proprietor', 'unincorporated_association', 'other')),
    -- 法人番号（13 桁）。任意（docs/13 §2-3。必須にすると迂回のダミー値が入る）。
    -- 検査数字の検証はアプリ側。DB は桁と数字だけを見る。
    corporate_number            TEXT CHECK (corporate_number IS NULL OR corporate_number GLOB '[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]'),
    -- 名寄せの手動キー（docs/13 §2-2）。自分自身は束ねられない。
    -- 2 段より深い連鎖（A→B→C）や循環（A→B→A）は行をまたぐので DB では見ない。
    -- 解決の順序（法人番号 → 親 → 自分）はアプリ側が持つ。
    parent_partner_id           INTEGER REFERENCES partners(id) CHECK (parent_partner_id IS NULL OR parent_partner_id <> id),

    -- 個人事業者に法人番号は指定されない（リサーチで確認済みの制度事実）。
    -- 矛盾した行を許すと、法人番号が自然キーとして名寄せされ、誤束ねが黙って起こる。
    CHECK (entity_type IS NULL OR entity_type <> 'sole_proprietor' OR corporate_number IS NULL)
);

INSERT INTO partners (id, code, name, name_kana, is_active, display_order,
                      created_at, updated_at, creator, updater, optimistic_locking,
                      address, entity_type, corporate_number, parent_partner_id)
    SELECT id, code, name, name_kana, is_active, display_order,
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

-- ② partner_invoice_registrations の作り直し
CREATE TABLE pir_rebuild AS SELECT * FROM partner_invoice_registrations;
CREATE TABLE pir_rebuild_seq AS SELECT seq FROM sqlite_sequence WHERE name = 'partner_invoice_registrations';
DROP TABLE partner_invoice_registrations;

CREATE TABLE partner_invoice_registrations (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    partner_id                  INTEGER NOT NULL REFERENCES partners(id),

    -- 登録番号。書式の検査はアプリ側で行う（「T ＋ 13 桁」という通念は一次情報で未確認。
    -- リサーチ §5。確認してから検査を書く。DB で中途半端な書式を強制しない）
    registration_no             TEXT NOT NULL,

    -- 登録年月日（公表システムの registrationDate）
    valid_from                  DATE NOT NULL,
    -- 取消・失効の年月日（disposalDate / expireDate）。登録が生きていれば NULL。
    -- **この日を含むかどうか（有効期間の閉じ方）はフェーズ 3 で制度を確認してから実装する。**
    -- 提供された日付をそのまま持ち、判定の規則をデータに焼き込まない
    ended_on                    DATE,
    -- expired ＝ expireDate（失効年月日）由来、revoked ＝ disposalDate（取消年月日）由来。
    -- 公表システムが別項目で提供するとおり別の事象として持つ。
    -- **それぞれが指す法的事象（届出・職権・事業廃止など）の対応は未確認**（リサーチ §5）。
    end_reason                  TEXT CHECK (end_reason IN ('expired', 'revoked')),

    -- 出所。手で入れた値を自動同期（フェーズ 6）が黙って上書きしないため
    source                      TEXT NOT NULL DEFAULT 'manual' CHECK (source IN ('manual', 'nta_api', 'nta_download')),
    confirmed_on                DATE,               -- この行を最後に確かめた日
    nta_updated_on              DATE,               -- 公表システム側の更新年月日（updateDate）

    -- 公表名（name）。マスタ名とずれることがあり（略称で登録している等）、突合の結果として残す。
    -- 個人のダウンロード提供では値が削除される項目があるため（どの項目かは未確認。リサーチ §5）、
    -- 空で来ることがある
    published_name              TEXT,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    -- 認証部品（app_users）の識別子。**外部キーを張らない。**（004 の partners と同じ理由）
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    -- 終わりには必ず理由がある。理由だけ・終わりだけの行を作らない
    CHECK ((ended_on IS NULL) = (end_reason IS NULL)),
    -- 登録より前には終われない（転記ミスの検査。同日はあり得るかもしれないので許す）
    CHECK (ended_on IS NULL OR ended_on >= valid_from),
    -- 同じ取引先に同じ登録の行を二重に取り込まない
    UNIQUE (partner_id, registration_no, valid_from)
);

INSERT INTO partner_invoice_registrations (id, partner_id, registration_no, valid_from, ended_on, end_reason,
                                           source, confirmed_on, nta_updated_on, published_name,
                                           created_at, updated_at, creator, updater, optimistic_locking)
    SELECT id, partner_id, registration_no, valid_from, ended_on, end_reason,
           source, confirmed_on, nta_updated_on, published_name,
           created_at, updated_at, creator, updater, optimistic_locking
    FROM pir_rebuild;
DROP TABLE pir_rebuild;
UPDATE sqlite_sequence SET seq = max(seq, (SELECT seq FROM pir_rebuild_seq))
 WHERE name = 'partner_invoice_registrations' AND EXISTS (SELECT 1 FROM pir_rebuild_seq);
INSERT INTO sqlite_sequence (name, seq)
    SELECT 'partner_invoice_registrations', seq FROM pir_rebuild_seq
    WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'partner_invoice_registrations');
DROP TABLE pir_rebuild_seq;
