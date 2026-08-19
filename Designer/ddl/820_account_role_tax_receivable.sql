-- 820_account_role_tax_receivable.sql — 仮払消費税に account_role を与える（BUG-0446）
--
-- ddl/700 で仮受消費税（2200）には `consumption_tax_payable` を割り当てたが、
-- 対になる仮払消費税（1900）には役割が無く、税行生成の中核（JournalEntry.RegenerateTaxLines）が
-- コード '1900' の直書きで拾っていた。
--
-- 科目体系を組み替えた導入先でコードが変わると、税行の account_id が NULL のまま NOT NULL に当たり、
-- Submit が false。ユーザーに出るのは「ほかの人が同時に伝票を確定した可能性があります」という
-- **無関係な文言**で、何度押しても直らない。

UPDATE accounts SET account_role = 'consumption_tax_receivable'
 WHERE code = '1900' AND COALESCE(account_role, '') = ''
   AND NOT EXISTS (SELECT 1 FROM accounts a2 WHERE a2.account_role = 'consumption_tax_receivable');

SELECT code, name, account_type, account_role FROM accounts
 WHERE account_role IS NOT NULL ORDER BY code;
