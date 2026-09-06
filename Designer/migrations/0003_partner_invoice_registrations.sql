-- 適格請求書発行事業者の登録テーブルを足す（docs/13 §3）。
-- 正典は ddl/006_partner_registrations.sql（CREATE 文はそこからの逐語の写し）。
CREATE TABLE partner_invoice_registrations (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,

    partner_id                  INTEGER NOT NULL REFERENCES partners(id),

    registration_no             TEXT NOT NULL,

    valid_from                  DATE NOT NULL,
    ended_on                    DATE,
    end_reason                  TEXT CHECK (end_reason IN ('expired', 'revoked')),

    source                      TEXT NOT NULL DEFAULT 'manual' CHECK (source IN ('manual', 'nta_api', 'nta_download')),
    confirmed_on                DATE,
    nta_updated_on              DATE,

    published_name              TEXT,

    created_at                  DATETIME,
    updated_at                  DATETIME,
    creator                     INTEGER,
    updater                     INTEGER,
    optimistic_locking          INTEGER NOT NULL DEFAULT 0,

    CHECK ((ended_on IS NULL) = (end_reason IS NULL)),
    UNIQUE (partner_id, registration_no, valid_from)
);

CREATE INDEX ix_partner_invoice_registrations_partner ON partner_invoice_registrations (partner_id);
