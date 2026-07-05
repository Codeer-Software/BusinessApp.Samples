-- 100_roles.sql — 権限3ロール（B-8、AccountingSQLite）
-- ロール: general(一般社員) / approver(承認者=課長・部長) / accounting(経理)
-- A=会計コア（仕訳・帳票・会計マスタ・設定）は accounting のみ閲覧可（各モジュールの UserReadCondition）。
-- 共有マスタ（取引先/部門/プロジェクト/費目）・テンプレ・ユーザー管理は閲覧全員・書込 accounting。

ALTER TABLE app_users ADD COLUMN role TEXT;

UPDATE app_users SET role = 'accounting' WHERE user_name IN ('admin', 'soumu');
UPDATE app_users SET role = 'approver'   WHERE user_name IN ('hanako', 'jiro');
UPDATE app_users SET role = 'general'    WHERE role IS NULL;
