-- 721: 制度閾値の「いま有効な行」を 1 か所で解決するビュー（BUG-0337 / BUG-0351）
--
-- system_thresholds は同じ code を **改正のたびに行を足して** 期間で切り替える設計になっている
-- （実例: SME_IMMEDIATE は 300,000 円（〜2026-03-31）と 400,000 円（2026-04-01〜・令和8年度改正）の 2 行）。
-- ところが読み手の多くは `WHERE code = 'X' LIMIT 1` と書いており、**期間を見ていない**。
-- 改正行を足した瞬間に、どちらが当たるかが行の並び順まかせになる（静かに古い値を使い続ける）。
--
-- 判定を 1 か所に畳む。読み手は `SELECT amount FROM v_system_threshold_current WHERE code = 'X'` と書く。
-- 既定値（マスタに行が無いときのフォールバック）は用途ごとに違うので、読み手側の COALESCE に残す。
DROP VIEW IF EXISTS v_system_threshold_current;
CREATE VIEW v_system_threshold_current AS
SELECT t.id, t.code, t.name, t.amount, t.unit, t.valid_from, t.valid_to
FROM system_thresholds t
WHERE t.id = (
  SELECT t2.id FROM system_thresholds t2
  WHERE t2.code = t.code
    AND (t2.valid_from IS NULL OR date(t2.valid_from) <= date('now', 'localtime'))
    AND (t2.valid_to   IS NULL OR date(t2.valid_to)   >= date('now', 'localtime'))
  -- 期間が重なる行が複数あっても 1 行に決める: 開始日が新しいほう、同着なら後から登録したほう
  ORDER BY COALESCE(date(t2.valid_from), '0001-01-01') DESC, t2.id DESC
  LIMIT 1
);
