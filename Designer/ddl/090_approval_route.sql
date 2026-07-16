-- 090_approval_route.sql — 承認ルート自動決定（B2-3、AccountingSQLite）
-- 設計: docs/07_経費精算設計.md §3 / 台帳 B2-3
-- どのテンプレートを使うかの判定は 2026-07-14 以降 approval_route_rules（260・ADR-0023）が正。
-- 判定額 = 立替精算は金額、事前申請は見込み額（実費確定後は実費）

-- 承認者の役職指定（テンプレメンバー拡張）
-- NULL/'' = approver_user_id の指定ユーザーそのまま / 'manager' = 申請者の部門の課長 / 'director' = 同・部長
ALTER TABLE approval_flow_template_member ADD COLUMN approver_role TEXT;

-- 承認ルート閾値 EXP_APPROVAL_MID/HIGH は 2026-07-14 に廃止（ADR-0023）。
-- ルート選択は 260_approval_route_rules.sql の approval_route_rules（費目×金額範囲→テンプレート）に一本化した。
-- B2-5: 事前申請の実費が見込み×この率(%)を超えたら再承認
INSERT INTO system_thresholds (code, name, amount, valid_from, valid_to)
SELECT 'EXP_OVERRUN_RATE', '経費精算: 実費超過の再承認率(%)', 110, NULL, NULL
WHERE NOT EXISTS (SELECT 1 FROM system_thresholds WHERE code = 'EXP_OVERRUN_RATE');

-- ---- テンプレ4種 seed（既存 SimpleExpense は過去データが参照するため残置・未使用化） ----
-- 経費_課長のみ
INSERT INTO approval_flow_template (name, description)
SELECT '経費_課長のみ', '3万円未満の経費。申請者の部門の課長が承認'
WHERE NOT EXISTS (SELECT 1 FROM approval_flow_template WHERE name = '経費_課長のみ');
INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 0 FROM approval_flow_template t
WHERE t.name = '経費_課長のみ'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o WHERE o.template_id = t.id);
INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, NULL, 'manager'
FROM approval_flow_template t JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 0
WHERE t.name = '経費_課長のみ'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);

-- 経費_部長のみ
INSERT INTO approval_flow_template (name, description)
SELECT '経費_部長のみ', '3万円以上20万円未満の経費。申請者の部門の部長が承認'
WHERE NOT EXISTS (SELECT 1 FROM approval_flow_template WHERE name = '経費_部長のみ');
INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 0 FROM approval_flow_template t
WHERE t.name = '経費_部長のみ'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o WHERE o.template_id = t.id);
INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, NULL, 'director'
FROM approval_flow_template t JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 0
WHERE t.name = '経費_部長のみ'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);

-- 経費_課長＋総務（交際費など。課長 → 総務の2段）
INSERT INTO approval_flow_template (name, description)
SELECT '経費_課長＋総務', '3万円未満の交際費等。課長承認の後に総務が確認'
WHERE NOT EXISTS (SELECT 1 FROM approval_flow_template WHERE name = '経費_課長＋総務');
INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 0 FROM approval_flow_template t
WHERE t.name = '経費_課長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o WHERE o.template_id = t.id AND o.order_no = 0);
INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 1 FROM approval_flow_template t
WHERE t.name = '経費_課長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o WHERE o.template_id = t.id AND o.order_no = 1);
INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, NULL, 'manager'
FROM approval_flow_template t JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 0
WHERE t.name = '経費_課長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);
INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, (SELECT id FROM app_users WHERE user_name = 'soumu_kacho1'), 'user'
FROM approval_flow_template t JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 1
WHERE t.name = '経費_課長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);

-- 経費_部長＋総務（20万円以上 or 3万以上の交際費。部長 → 総務の2段）
INSERT INTO approval_flow_template (name, description)
SELECT '経費_部長＋総務', '20万円以上の経費・3万円以上の交際費。部長承認の後に総務が確認'
WHERE NOT EXISTS (SELECT 1 FROM approval_flow_template WHERE name = '経費_部長＋総務');
INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 0 FROM approval_flow_template t
WHERE t.name = '経費_部長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o WHERE o.template_id = t.id AND o.order_no = 0);
INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 1 FROM approval_flow_template t
WHERE t.name = '経費_部長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o WHERE o.template_id = t.id AND o.order_no = 1);
INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, NULL, 'director'
FROM approval_flow_template t JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 0
WHERE t.name = '経費_部長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);
INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, (SELECT id FROM app_users WHERE user_name = 'soumu_kacho1'), 'user'
FROM approval_flow_template t JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 1
WHERE t.name = '経費_部長＋総務'
  AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);

-- 旧テストユーザー（hanako/jiro）の役職・所属 seed は 2026-07-09 の組織再編で廃止した。
-- 役職者は 255_org_managers.sql（department_managers）、所属は 085_org_users.sql が正。
-- ※departments.manager_user / director_user 列は 250 の移行後は使用しない旧仕様。
