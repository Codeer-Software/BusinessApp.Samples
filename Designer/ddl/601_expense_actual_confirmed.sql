-- 601_expense_actual_confirmed.sql — 事前申請の実費確定フラグ（ADR-0066 の実装中に判明した必要列）
--
-- 経緯: 明細行化により「事前申請でも明細（費目）を持たせる」必要が出た。
--   費目は承認ルートの判定条件（approval_route_rules）なので、明細が無い事前申請は
--   交際費であっても「全費目」ルールにしか当たらず、総務段が積み上がらない＝統制の穴になる。
--
-- そのため事前申請も申請時に明細を入れる形にし、見込み額（estimated_amount）は
-- 「申請時の明細合計のスナップショット」へ意味を変えた（手入力をやめる）。
-- 判定額は立替・事前を問わず常に「明細合計（amount）」に一本化される。
--
-- その結果「実費が確定したか」を amount > 0 で判別できなくなったため、明示的な列を持つ。
-- （settlement_status に新しい値を足す案は採らない——ポータル件数・精算処理待ちの SQL が
--   'approved'/'accounting'/'settled' を直接列挙しており、下流を無修正で保つ方針に反するため）
--
-- 注意: SQLite の ALTER TABLE ADD COLUMN は IF NOT EXISTS が使えない。
--       2 回目の実行は「duplicate column name」になるが、既に列がある＝適用済みなので害はない。

ALTER TABLE expense_request ADD COLUMN actual_confirmed INTEGER;

-- 既存データ: 実費確定を経て経理処理以降へ進んでいる事前申請は確定済みとして扱う
UPDATE expense_request
   SET actual_confirmed = 1
 WHERE request_type = 'advance'
   AND settlement_status IN ('accounting', 'settled', 'completed');
