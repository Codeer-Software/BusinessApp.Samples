-- 385_recompute_permission_cache.sql — 権限キャッシュ列の一括再計算（何度でも再実行可、BusinessAppSQLite）
-- app_users.has_sales_access / has_accounting_access / is_approver は department_members と
-- departments.dept_type から導出されるキャッシュ（正典は 380 のトリガー）。
-- トリガーを迂回する経路（DB ファイル差し替え・トリガー無効化中の操作など）の後や、
-- 整合が疑わしいときにこれを流せば必ず正しい状態に戻る。is_sysadmin は真実列なので触らない。

UPDATE app_users SET
  is_approver = EXISTS(SELECT 1 FROM department_members m
                       WHERE m.user_id = app_users.id AND m.role IN ('manager','director')),
  has_sales_access = EXISTS(SELECT 1 FROM department_members m
                            JOIN departments d ON d.id = m.department_id
                            WHERE m.user_id = app_users.id AND d.dept_type = 'sales'),
  has_accounting_access = EXISTS(SELECT 1 FROM department_members m
                                 JOIN departments d ON d.id = m.department_id
                                 WHERE m.user_id = app_users.id AND d.dept_type = 'accounting');
