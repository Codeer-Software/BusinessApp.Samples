-- 740: 「1 つの伝票元に対して自動仕訳は 1 本」を DB で保証する（BUG-0353）
--
-- 自動仕訳の二重生成ガードは、どのモジュールも「先に SELECT して無ければ作る」形で書かれている。
-- 検査と登録のあいだに窓があるので、2 タブで同時に押す・二度押しが通る、といった経路で
-- **同じ伝票元から仕訳が 2 本立つ**（費用と買掛金の二重計上）。アプリ側のガードは残したまま、
-- 最後の砦を DB に置く——競合したほうは登録に失敗し、静かに二重計上されることが無くなる。
--
-- **1 元 1 本と言い切れる連動元だけ**を対象にする（部分ユニークインデックス）。
--   depreciation      … 年度ごとに 1 本ずつ立つ（資産 1 件に複数）
--   recurring_defer   … 前受収益の月次取崩しで毎月 1 本
--   template          … 定型仕訳は何度でも起票できる
--   ses / recurring*  … 1 元 1 本と読めるが確証が無いので今回は対象外にする（保守的に）
-- 実データで対象 10 種すべて max 1 本であることを確認してから作成している。
CREATE UNIQUE INDEX IF NOT EXISTS ux_journal_entries_source_single
ON journal_entries(source_type, source_id)
WHERE source_id IS NOT NULL
  AND source_type IN ('acceptance', 'bank', 'disposal', 'expense', 'expense_payment',
                      'receipt', 'vendor_invoice', 'vendor_payment', 'wip', 'wip_reversal');
