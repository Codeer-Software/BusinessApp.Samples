-- 385_recompute_permission_cache.sql — is_approver キャッシュの一括再計算（何度でも再実行可、BusinessAppSQLite）
-- ADR-0043 以降、has_sales_access / has_accounting_access は「直接付与の真実」（ユーザー管理画面で編集）と
-- なったため再計算の対象外。導出キャッシュは is_approver のみ:
--   is_approver = どこかの部門に課長/部長行がある ∨ 承認テンプレートの個人指名承認者である
-- （正典は 400 のトリガー。トリガーを迂回する経路の後や整合が疑わしいときにこれを流す）

UPDATE app_users SET
  is_approver = EXISTS(SELECT 1 FROM department_members m
                       WHERE m.user_id = app_users.id AND m.role IN ('manager','director'))
             OR EXISTS(SELECT 1 FROM approval_flow_template_member tm
                       WHERE tm.approver_user_id = app_users.id
                         AND (tm.approver_role = 'user' OR tm.approver_role IS NULL));
