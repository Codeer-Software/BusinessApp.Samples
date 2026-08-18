-- 690_cash_alert_threshold.sql — 資金繰りの危険水域の閾値（BUG-0249(a)・開発者判断 2026-08-18）
--
-- 【問題】資金繰り予測の警告条件が「期末資金 < 0」だけで、**マイナスに転じた月に初めて鳴る**。
-- 資金繰り警告は「足りなくなる前に手を打つ」ための機能なので、手遅れになってから鳴るのは
-- 目的を果たしていない。残高が 30 万円まで落ちても無警告だった。
--
-- 【方式】閾値はマスタに置く（税率・各種上限と同じ流儀。CLAUDE.md §3「ハードコードしない」）。
-- 既定 3,000,000 円＝ペルソナ（IT 受託 30〜80 名）の**月次固定費のおよそ 1 か月分**を目安にした。
-- 実運用では各社が自社の固定費に合わせて設定する。0 を入れれば従来どおり「マイナスのときだけ」になる。
INSERT INTO system_thresholds (code, name, amount, unit)
SELECT 'CASH_ALERT_BALANCE', '資金繰りの危険水域（期末資金がこの額を下回ると警告）', 3000000, 'yen'
WHERE NOT EXISTS (SELECT 1 FROM system_thresholds WHERE code = 'CASH_ALERT_BALANCE');
