-- 105_org_roles.sql — 標準組織のロール設定（100_roles.sql の後に適用）
-- sysadmin(システム管理者=admin) / accounting(経理=総務部全員) / approver(役職者) / general(一般)
-- 100_roles.sql 末尾が「role IS NULL → general」で全員 general にするため、ここで上書きする

UPDATE app_users SET role = 'sysadmin' WHERE user_name = 'admin';

-- 総務部は全員が経理担当
UPDATE app_users SET role = 'accounting' WHERE user_name IN ('soumu_bucho', 'soumu_buchodairi', 'soumu_kacho1', 'soumu_kacho2', 'soumu_ippan');

-- 総務部以外の役職者（部長・部長代理・課長）は承認者
UPDATE app_users SET role = 'approver' WHERE user_name IN ('eigyo_bucho', 'eigyo_buchodairi', 'eigyo_kacho1', 'eigyo_kacho2', 'kaihatsu1_bucho', 'kaihatsu1_buchodairi', 'kaihatsu1_kacho1', 'kaihatsu1_kacho2', 'kaihatsu2_bucho', 'kaihatsu2_buchodairi', 'kaihatsu2_kacho1', 'kaihatsu2_kacho2', 'saas_bucho', 'saas_buchodairi', 'saas_kacho1', 'saas_kacho2');

-- 一般社員（総務部以外の ippan）は general のまま（100_roles.sql が設定済み）
