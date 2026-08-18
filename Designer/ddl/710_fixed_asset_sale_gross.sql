-- 710_fixed_asset_sale_gross.sql — 固定資産の売却を総額法にする（BUG-0338 の続き・2026-08-18）
--
-- 【なぜ変えるか】差額（純額）で計上すると、**消費税の課税標準が「売却損益の額」になってしまう**。
-- 敵対的レビューの机上検算で、売却損のケースでは**借方の売却損が課税売上として ＋計上される**
-- （消費税集計表は `dc = accounts.dc_normal` を +1 とするため）ことが判明した。
-- 課税標準は「対価の額」でなければならない。
--
-- 【総額法】売却対価を「固定資産売却益」に、帳簿価額を「固定資産売却原価」に、それぞれ総額で立てる。
--   D 未収入金（対価＋税）／ D 固定資産売却原価（簿価）／ C 固定資産（簿価）／
--   C 固定資産売却益（対価・課税売上）／ C 仮受消費税
-- こうすると **課税標準＝対価**で一致し、貸借も損益の符号に関わらず必ず釣り合う（分岐が消える）。
-- PL 上は売却原価と売却益が両建てになるが、差引の純額は従来と同じである。
--
-- 8510 固定資産売却損は残す（手で起票する訂正などで使う）。
INSERT INTO accounts
    (code, name, kana, account_type, category_id, dc_normal,
     default_tax_category_id, display_order, is_active, is_cash_equivalent,
     is_fixed_asset_account, account_role)
SELECT '8520', '固定資産売却原価', NULL, 'expense',
       (SELECT id FROM account_categories WHERE code = 'EL'),      -- 特別損失
       'D',
       (SELECT id FROM tax_categories WHERE code = 'OUT_OF_SCOPE'),
       8520, 1, 0, 0, 'sale_cost'
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE code = '8520');
