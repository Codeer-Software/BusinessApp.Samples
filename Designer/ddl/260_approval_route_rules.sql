-- 260_approval_route_rules.sql — 承認ルート判定ルールのマスタ化（ADR-0023、AccountingSQLite）
-- 旧方式: ExpenseRequest.mod.cs の SelectTemplateName() が閾値（EXP_APPROVAL_MID/HIGH）と
--         交際費フラグから「テンプレート名の文字列」を直書き分岐で返していた。
-- 新方式: 本テーブルを priority 昇順に評価し、費目と金額（下限≦判定額≦上限）が
--         最初に一致した行の template_id を使う。テンプレートは ID 参照（改名に強い）。
-- 注意: FK 列に NOT NULL を付けない（Project.md 知見）。

CREATE TABLE IF NOT EXISTS approval_route_rules (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    priority INTEGER NOT NULL DEFAULT 0,                            -- 小さい順に評価し最初に一致した行を採用
    expense_category_id INTEGER REFERENCES expense_categories(id), -- NULL = 全費目に一致
    min_amount INTEGER NOT NULL DEFAULT 0,                          -- 判定額の下限（この額を含む。最低 0）
    max_amount INTEGER,                                             -- 判定額の上限（この額を含む）。NULL = 上限なし
    template_id INTEGER REFERENCES approval_flow_template(id),     -- 適用する承認フローテンプレート
    note TEXT,                                                      -- 説明
    is_active INTEGER NOT NULL DEFAULT 1
);

-- ---- seed: 旧ハードコード分岐の移行（MID=3万・HIGH=20万・交際費は総務併載） ----
-- 交際費フラグ（is_entertainment=1）の費目ごとに個別ルールを生成（現状は ENT のみ）
INSERT INTO approval_route_rules (priority, expense_category_id, min_amount, max_amount, template_id, note, is_active)
SELECT 10, c.id, 0, 29999,
       (SELECT id FROM approval_flow_template WHERE name = '経費_課長＋総務'),
       c.name || '（3万円未満）: 課長承認の後に総務が確認', 1
FROM expense_categories c
WHERE c.is_entertainment = 1
  AND NOT EXISTS (SELECT 1 FROM approval_route_rules r
                  WHERE r.expense_category_id = c.id AND r.min_amount = 0);
INSERT INTO approval_route_rules (priority, expense_category_id, min_amount, max_amount, template_id, note, is_active)
SELECT 20, c.id, 30000, NULL,
       (SELECT id FROM approval_flow_template WHERE name = '経費_部長＋総務'),
       c.name || '（3万円以上）: 部長承認の後に総務が確認', 1
FROM expense_categories c
WHERE c.is_entertainment = 1
  AND NOT EXISTS (SELECT 1 FROM approval_route_rules r
                  WHERE r.expense_category_id = c.id AND r.min_amount = 30000);

-- 全費目の既定 3 段（金額のみで決まる従来ルート）
INSERT INTO approval_route_rules (priority, expense_category_id, min_amount, max_amount, template_id, note, is_active)
SELECT 100, NULL, 0, 29999,
       (SELECT id FROM approval_flow_template WHERE name = '経費_課長のみ'),
       '3万円未満: 課長決裁', 1
WHERE NOT EXISTS (SELECT 1 FROM approval_route_rules
                  WHERE expense_category_id IS NULL AND min_amount = 0);
INSERT INTO approval_route_rules (priority, expense_category_id, min_amount, max_amount, template_id, note, is_active)
SELECT 110, NULL, 30000, 199999,
       (SELECT id FROM approval_flow_template WHERE name = '経費_部長のみ'),
       '3万円以上20万円未満: 部長決裁', 1
WHERE NOT EXISTS (SELECT 1 FROM approval_route_rules
                  WHERE expense_category_id IS NULL AND min_amount = 30000);
INSERT INTO approval_route_rules (priority, expense_category_id, min_amount, max_amount, template_id, note, is_active)
SELECT 120, NULL, 200000, NULL,
       (SELECT id FROM approval_flow_template WHERE name = '経費_部長＋総務'),
       '20万円以上: 部長承認の後に総務が確認', 1
WHERE NOT EXISTS (SELECT 1 FROM approval_route_rules
                  WHERE expense_category_id IS NULL AND min_amount = 200000);

-- ---- 旧閾値の廃止: 承認ルート選択は本マスタに一本化（EXP_OVERRUN_RATE 等の他用途は残置） ----
DELETE FROM system_thresholds WHERE code IN ('EXP_APPROVAL_MID', 'EXP_APPROVAL_HIGH');
