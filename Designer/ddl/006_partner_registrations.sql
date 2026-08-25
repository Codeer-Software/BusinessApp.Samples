-- 006 適格請求書発行事業者の登録（docs/07 §3）
--
-- 登録は取消・失効・再登録があり、partners の 1 列では「その時点でどうだったか」を表せない。
-- 有効期間つきの行で持ち、引く日付は journal_lines.tax_point（課税仕入れを行った日）。
-- 列は公表システムの提供項目（リソース定義書 1.5 版の目次で確認。
-- docs/research/2026-08-25_取引先の識別番号と公表システム.md §3-2）に対応させてある。

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
-- partner_id 単独の索引は置かない。UNIQUE の複合索引の先頭列が partner_id なので賄える。
