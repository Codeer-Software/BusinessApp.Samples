-- 819_account_roles_ar_trade_payable.sql — 売掛金・買掛金に account_role を与える（BUG-0439）
--
-- 818 で未払金（2020）と給料手当（6010）に役割を与えたのと同じ趣旨。
-- ポータル KPI の「売掛金」「買掛金」がまだ科目コードの直書き（'1100' / '2000'）で、
-- 導入先が別のコード体系を使うと**無言で 0 円**になる。SQL なので警告も出ない。
--
-- **未払金（2020）と買掛金（2000）は別物**なので役割名も分ける:
--   accounts_payable … 未払金（経費・仕入以外の債務）        ← 818 で付与済み
--   trade_payable    … 買掛金（仕入債務）
--   accounts_receivable … 売掛金（1110 未収入金＝disposal_receivable とは別）

UPDATE accounts SET account_role = 'accounts_receivable'
 WHERE code = '1100' AND COALESCE(account_role, '') = ''
   AND NOT EXISTS (SELECT 1 FROM accounts a2 WHERE a2.account_role = 'accounts_receivable');

UPDATE accounts SET account_role = 'trade_payable'
 WHERE code = '2000' AND COALESCE(account_role, '') = ''
   AND NOT EXISTS (SELECT 1 FROM accounts a2 WHERE a2.account_role = 'trade_payable');

SELECT code, name, account_type, account_role FROM accounts
 WHERE account_role IS NOT NULL ORDER BY code;
