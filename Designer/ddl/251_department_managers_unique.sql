-- 251_department_managers_unique.sql — 部門役職者の重複登録防止（ADR-0016 レビュー指摘対応、BusinessAppSQLite)
-- 同一(部門, ユーザー, 役職)の二重登録を DB レベルで禁止する。
-- ルート解決側にも重複除去はあるが（ApprovalFlow.ResolveDeptRoleAll）、マスタのデータ品質は DB で担保する。
-- 注意: 部門マスタ画面で重複行を追加して保存するとエラーになる（正しい挙動。行を1つに直して保存し直す）。
CREATE UNIQUE INDEX IF NOT EXISTS ux_department_managers_dept_user_role
    ON department_managers(department_id, user_id, role);
