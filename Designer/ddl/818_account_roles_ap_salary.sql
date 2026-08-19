-- 818_account_roles_ap_salary.sql — 未払金・給与手当に account_role を与える（BUG-0429 / BUG-0434）
--
-- `accounts.account_role`（ddl/630）は「この科目がアプリのどの役割を担うか」を科目マスタ側に持たせる仕組み。
-- 1 役割 1 科目のユニーク制約つき。科目コードの直書きをやめるための正典。
--
-- ここで 2 つ足す:
--   accounts_payable … 資金繰り予測の「未払金残高」。これまで SQL が `a.code = '2020'` を直書きしていた。
--                      導入先が別の科目コード体系を使うと**無言で 0 円**になり、当月の出金予定が消える
--                      （スクリプト側は「未払金(2020)の科目がありません」とトーストで落ちるのに、SQL は黙る）
--   salary_expense  … 給与の支払仕訳を見分けるため。資金繰り予測は monthly_salaries から人件費を
--                      出金に積むが、給与の仕訳を切った月はその出金が現預金残高にも反映されている。
--                      「もう払ったか」を判定するのにこの役割を使う
--
-- 既に別の科目に割り当てられている場合は何もしない（ユニーク制約に当たらないよう空のときだけ入れる）。

UPDATE accounts SET account_role = 'accounts_payable'
 WHERE code = '2020' AND COALESCE(account_role, '') = ''
   AND NOT EXISTS (SELECT 1 FROM accounts a2 WHERE a2.account_role = 'accounts_payable');

UPDATE accounts SET account_role = 'salary_expense'
 WHERE code = '6010' AND COALESCE(account_role, '') = ''
   AND NOT EXISTS (SELECT 1 FROM accounts a2 WHERE a2.account_role = 'salary_expense');

SELECT code, name, account_type, account_role FROM accounts
 WHERE account_role IS NOT NULL ORDER BY code;
