-- 670_project_department.sql — 案件に担当部門を持たせる（BUG-0061・開発者判断 2026-08-18）
--
-- 【問題】SES 精算から作られる請求書と売上仕訳が、明示的に「全社共通」部門で起票されていた
-- （`SesBilling.mod.cs:354`）。全社共通は費用の共通配賦用の受け皿なので、
-- **SES の売上が部門別 P/L に一切乗らない**。原因は SES 請求に部門の取得元が無いこと。
--
-- 【なぜ案件に持たせるか】定期請求契約（`recurring_billings`）は **既に `department_id` を持っている**。
-- SES の契約実体は案件そのものなので、「契約に担当部門を持たせる」＝「案件に持たせる」になる。
-- 部門と案件の 3 軸分離（ADR-0043）は保たれる——この列は**起票時の既定値の取得元**であって、
-- 伝票の部門は今も伝票が持つ（ADR-0029）。上下関係を作るものではない。
--
-- NULL 許容。空なら従来どおり「全社共通」に落ちる（後退経路を残す）。
ALTER TABLE projects ADD COLUMN department_id INTEGER REFERENCES departments(id);

-- ---- 既存の SES 案件を埋める（工数の入力者の所属部から導く） ----
-- 収益を計上すべき部門は「その案件に人を出している部門」である。実データではそれを
-- 直接持つ列が無いので、**工数を入力している人の所属部**を唯一の根拠として使う。
-- 複数部門が入力している案件は最も工数の多い部門を採る（現データでは各案件 1 部門のみ）。
UPDATE projects
SET department_id = (
  SELECT u.business_department_id
  FROM time_entries t
  JOIN app_users u ON u.id = t.user_id
  WHERE t.project_id = projects.id AND u.business_department_id IS NOT NULL
  GROUP BY u.business_department_id
  ORDER BY SUM(t.minutes) DESC
  LIMIT 1
)
WHERE project_type = 'ses' AND department_id IS NULL;
