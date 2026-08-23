-- 003 税区分（ADR-0006 ③ 会計マスタ）
--
-- ペルソナは課税事業者・本則課税・税抜経理・個別対応方式（docs/02 §3）。
-- 簡易課税・2 割特例で使う区分は作らない（ADR-0007 の機能スコープ）。
--
-- **名前に税率の数値を書かない。** 「課税売上（標準税率）」であって「課税売上 10%」ではない。
-- その日に何 % かは制度ルールが決めるので、数値を名前に埋めると改正の日にマスタ名が嘘になる。
-- 画面では rate_kind から「10%」等を表示すればよい（表示は制度ルール由来）。
--
-- 用途区分（default_tax_treatment）は**あえて空にしてある**。個別対応方式では
-- 課税売上対応／共通対応／非課税売上対応を取引ごとに選ぶ必要があり、
-- 既定値で埋めると「値は入っているが意味がない」状態を作る（docs/04 §9-1）。

INSERT INTO tax_categories (code, name, taxation_type, rate_kind, display_order) VALUES
    ('OUT',  '対象外',                     'out_of_scope',     NULL,       10),
    ('TS',   '課税売上（標準税率）',       'taxable_sales',    'standard', 20),
    ('TSR',  '課税売上（軽減税率）',       'taxable_sales',    'reduced',  30),
    ('TS8',  '課税売上（旧税率8%）',       'taxable_sales',    'legacy_8', 40),
    ('TP',   '課税仕入（標準税率）',       'taxable_purchase', 'standard', 50),
    ('TPR',  '課税仕入（軽減税率）',       'taxable_purchase', 'reduced',  60),
    ('TP8',  '課税仕入（旧税率8%）',       'taxable_purchase', 'legacy_8', 70),
    ('NTS',  '非課税売上',                 'non_taxable',      NULL,       80),
    ('NTP',  '非課税仕入',                 'non_taxable',      NULL,       90),
    ('EXP',  '免税売上（輸出）',           'export_exempt',    NULL,      100);
