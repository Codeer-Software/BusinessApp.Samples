-- 700_fixed_asset_sale_tax.sql — 固定資産の売却に消費税を載せる（BUG-0338・開発者判断 2026-08-18）
--
-- 【問題】売却仕訳が全明細「対象外」で起票され、**仮受消費税が 1 円も立たなかった**。
-- 入力欄が「売却価額（税抜）」と明示しているのに税額が帳簿にも消費税集計表にも現れず、
-- 課税事業者なら申告額が過少になる。
--
-- 【決定】**売却時に税区分を選ばせる**（既定＝売上の既定税区分）。一律に課税とすると
-- **土地の売却（非課税）**を誤るため、区分を持たせるのが筋（開発者判断）。
ALTER TABLE fixed_assets ADD COLUMN disposal_tax_category_id INTEGER REFERENCES tax_categories(id);

-- 仮受消費税の科目も役割で引けるようにする（コード直値をやめる・ddl/630 と同じ方針）
UPDATE accounts SET account_role = 'consumption_tax_payable' WHERE code = '2200'
  AND NOT EXISTS (SELECT 1 FROM accounts a2 WHERE a2.account_role = 'consumption_tax_payable' AND a2.code <> '2200');
