-- ポータル「自分あて」件数（ADR-0045・docs/13 §3 #2-#3 の契約）
-- 有効ユーザーごとに 1 行。PortalHome が自分の行だけを読む（ユーザー数は小規模なので全行で問題ない）。
-- あなたの承認待ち = approval_inbox_view（Active な Order で自分が Waiting・ADR-0016）
-- 進行中のあなたの申請 = 自分が起案した申請中の経費（旧 ExpenseHome と同一定義）
SELECT
  u.id AS user_id,
  (SELECT count(*) FROM approval_inbox_view v WHERE v.current_approver = u.id) AS my_approvals,
  (SELECT count(*) FROM expense_request e
    WHERE e.creator = u.id AND e.settlement_status = 'applying') AS my_applying
FROM app_users u
WHERE u.is_active = 1
