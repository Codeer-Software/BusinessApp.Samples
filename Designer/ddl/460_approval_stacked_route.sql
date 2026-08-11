-- 460_approval_stacked_route.sql — 承認ルートを「積み上げ方式」へ（ADR-0048・2026-08-09）
--
-- 変更前（090 の seed）: 金額が上がると承認者が課長から部長へ「入れ替わる」
-- 変更後: 金額が上がると承認段が「積み上がる」（課長 → 部長 → 総務）
--
-- 理由は ADR-0048。要点は 2 つ:
--   1. 日本の稟議の通例。上位者は下位者の承認を経て回ってくる。
--      入れ替え方式だと自分の課の支出を課長が知らないまま部長決裁になる
--   2. ADR-0044 の「重複段の圧縮」が初めて機能する。課長空席なら部長へ繰り上がり、
--      次の部長段と同じ人になるので段が 1 つに畳まれる。課長本人の申請も同じ理屈で
--      特例を書かずに正しく振る舞う
--
-- テンプレート名は「〜まで」= 役職ラインの終端、「＋総務」= 合議段、という規約に統一する。
--   経費_課長まで        : 課長
--   経費_部長まで        : 課長 → 部長
--   経費_課長まで＋総務  : 課長 → 総務
--   経費_部長まで＋総務  : 課長 → 部長 → 総務
--
-- approval_route_rules（260）の割り当ては変更不要（同じテンプレートを指したまま意味が変わる）。
-- テンプレートは申請時にコピーされるため、進行中・過去の申請には影響しない。
-- 冪等（何度実行しても同じ結果）。

-- ---- 1) 改名 ----
UPDATE approval_flow_template SET name = '経費_課長まで',
       description = '3万円未満の経費。申請者の課の課長が承認'
 WHERE name = '経費_課長のみ';

UPDATE approval_flow_template SET name = '経費_部長まで',
       description = '3万円以上20万円未満の経費。課長 → 部長の順に承認'
 WHERE name = '経費_部長のみ';

UPDATE approval_flow_template SET name = '経費_課長まで＋総務',
       description = '3万円未満の交際費等。課長承認の後に総務が確認'
 WHERE name = '経費_課長＋総務';

UPDATE approval_flow_template SET name = '経費_部長まで＋総務',
       description = '20万円以上の経費・3万円以上の交際費。課長 → 部長の後に総務が確認'
 WHERE name = '経費_部長＋総務';

-- ---- 2) 「経費_部長まで」に課長段を先頭へ挿入（既に 2 段なら何もしない） ----
UPDATE approval_flow_template_order
   SET order_no = order_no + 1
 WHERE template_id = (SELECT id FROM approval_flow_template WHERE name = '経費_部長まで')
   AND (SELECT COUNT(*) FROM approval_flow_template_order o2
         WHERE o2.template_id = approval_flow_template_order.template_id) = 1;

INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 0 FROM approval_flow_template t
 WHERE t.name = '経費_部長まで'
   AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o
                    WHERE o.template_id = t.id AND o.order_no = 0);

INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, NULL, 'manager'
  FROM approval_flow_template t
  JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 0
 WHERE t.name = '経費_部長まで'
   AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);

-- ---- 3) 「経費_部長まで＋総務」に課長段を先頭へ挿入（既に 3 段なら何もしない） ----
UPDATE approval_flow_template_order
   SET order_no = order_no + 1
 WHERE template_id = (SELECT id FROM approval_flow_template WHERE name = '経費_部長まで＋総務')
   AND (SELECT COUNT(*) FROM approval_flow_template_order o2
         WHERE o2.template_id = approval_flow_template_order.template_id) = 2;

INSERT INTO approval_flow_template_order (template_id, order_no)
SELECT t.id, 0 FROM approval_flow_template t
 WHERE t.name = '経費_部長まで＋総務'
   AND NOT EXISTS (SELECT 1 FROM approval_flow_template_order o
                    WHERE o.template_id = t.id AND o.order_no = 0);

INSERT INTO approval_flow_template_member (template_order_id, is_required, approver_user_id, approver_role)
SELECT o.id, 1, NULL, 'manager'
  FROM approval_flow_template t
  JOIN approval_flow_template_order o ON o.template_id = t.id AND o.order_no = 0
 WHERE t.name = '経費_部長まで＋総務'
   AND NOT EXISTS (SELECT 1 FROM approval_flow_template_member m WHERE m.template_order_id = o.id);

-- ---- 確認クエリ（手動実行用） ----
-- SELECT t.name, o.order_no, m.approver_role, u.user_name
--   FROM approval_flow_template t
--   JOIN approval_flow_template_order o ON o.template_id = t.id
--   JOIN approval_flow_template_member m ON m.template_order_id = o.id
--   LEFT JOIN app_users u ON u.id = m.approver_user_id
--  ORDER BY t.id, o.order_no;
