-- 816_journal_partner.sql — 仕訳伝票に「取引先」を持たせる（BUG-0003・ADR-0076）
--
-- 電子帳簿保存法の電子取引データ保存は「**取引年月日・取引金額・取引先**」の 3 点で
-- 検索できることを求める。日付と金額は仕訳が持っているが、**取引先だけ持っていなかった**。
-- 売掛金・買掛金には取引先の補助科目があるが、費用科目には無いので代替にならない。
--
-- **ヘッダに 1 つ持つ**（明細ではない）。理由は ADR-0076:
--   1 伝票 1 取引先が原則で、自動生成の 6 経路（検収・請求・入金・仕入先請求・定期請求・SES）は
--   いずれも取引先が 1 つに定まる。明細に持たせると検索も表示も複雑になるわりに得るものが少ない。
--
-- NULL 可。給与仕訳・決算整理・振替のように相手のいない伝票があるため。

ALTER TABLE journal_entries ADD COLUMN partner_id INTEGER REFERENCES partners(id);

CREATE INDEX IF NOT EXISTS idx_journal_entries_partner ON journal_entries(partner_id);
