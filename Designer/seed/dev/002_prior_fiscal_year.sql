-- 開発・デモ専用。**実運用に投入しない**（ADR-0039）。
--
-- **前の年度（第 17 期）を 1 本足す。** 初期データ（001_organization_and_periods.sql）が作るのは
-- 第 18 期だけで、**開発機に会計年度が 1 期しか無いと確かめられないものがある**:
--
--   ・**年度をまたぐ取消・訂正の断り**——前の年度の取引なら、その年度を名乗って
--     申告への影響を告げる 1 文が出る（字は docs/10 §5。docs/11 §5-2。ADR-0066 の決定 10 の③・決定 16）
--   ・**一覧を会計年度で絞れること**（qa/04 の BOK-01。**2026-09-02 から半分しか踏めていなかった**）
--   ・**伝票番号が年度ごとの連番であること**（I-17）
--
-- **初期データ（seed/ 直下）には入れない。** 新しく会計コアを立ち上げた会社に
-- 「前の期」は無く、あれば嘘のデータになる（seed/README の「デモデータではない」）。
--
-- **締めない（status は open）。** 締めは画面が無く（フェーズ 4）、閉じると
-- この年度に伝票を作れなくなって、上の 3 つがどれも踏めない。
--
-- **優良な電子帳簿の適用開始日（premium_ledger_from）は入れない。**
-- 適用は第 18 期からという前提（001 が第 18 期の開始日を入れている）で、
-- 前の期まで遡って「要件を満たしていた」と書くのは事実に反する。
-- **この年度に計上すると、不変条件 I-12（翌期首残高＝前期末残高（繰越後））がまだ当たらない状態になる。**
-- 期首残高も繰越もフェーズ 4 で未実装で、「繰越後」がそもそも無いからである（docs/10 §1・docs/15 §3）。
-- **繰越を作る回に、この開発データを前提にした試験例を用意する**（docs/04 §5）。
--
-- **伝票番号は年度ごとの連番（I-17）なので、第 17 期にも「1」がある。**
-- 画面に出る番号は年度を名乗らないので、**断りの中の番号を一覧で探すと 2 件当たりうる**（docs/04 §5）。
--
-- 何度流してもよい。**既にあるものは作り直さず、足りないものだけを足す**
-- （年度・期間・採番をそれぞれ別に守るので、途中で落ちた状態から流し直しても揃う）。

-- 第 17 期（2025-04-01 〜 2026-03-31）。3 月決算は第 18 期と同じ。
INSERT INTO fiscal_years (code, label, start_date, end_date, status)
SELECT 'FY17', '第 17 期（2025 年度）', '2025-04-01', '2026-03-31', 'open'
 WHERE NOT EXISTS (SELECT 1 FROM fiscal_years WHERE code = 'FY17');

-- 月次の会計期間 12 本。**1 本の INSERT で作る**——欠けた状態がそもそも存在しない。
--
-- **`UNION ALL` を使う。** `UNION` だと 2 行を同じに書き間違えたとき**黙って 11 本になる**が、
-- `UNION ALL` なら UNIQUE (fiscal_year_id, start_date) に当たって**その場で落ちる**。
--
-- **日付は date() で突き合わせる。** CLB は日付の列に時刻を付けて書く
-- （`2025-04-01 00:00:00`。qa/03 L-12）ので、**文字列一致だと NOT EXISTS の重複検査がすり抜ける**。
-- 外れると 12 本が二重に入り、FiscalCalendar が「会計期間が重複している」で落ちて
-- 計上も取消も保存もできなくなる。
INSERT INTO accounting_periods (fiscal_year_id, start_date, end_date, status)
SELECT y.id, m.start_date, m.end_date, 'open'
  FROM fiscal_years y
  JOIN (
            SELECT '2025-04-01' AS start_date, '2025-04-30' AS end_date
  UNION ALL SELECT '2025-05-01', '2025-05-31'
  UNION ALL SELECT '2025-06-01', '2025-06-30'
  UNION ALL SELECT '2025-07-01', '2025-07-31'
  UNION ALL SELECT '2025-08-01', '2025-08-31'
  UNION ALL SELECT '2025-09-01', '2025-09-30'
  UNION ALL SELECT '2025-10-01', '2025-10-31'
  UNION ALL SELECT '2025-11-01', '2025-11-30'
  UNION ALL SELECT '2025-12-01', '2025-12-31'
  UNION ALL SELECT '2026-01-01', '2026-01-31'
  UNION ALL SELECT '2026-02-01', '2026-02-28'
  UNION ALL SELECT '2026-03-01', '2026-03-31'
       ) m
 WHERE y.code = 'FY17'
   -- **年度の期間の外にある会計期間を、その年度に結び付けない。** 既に別の期間の FY17 があると、
   -- 年度の INSERT は 1 行も入らないのに 12 本はそのまま入る——
   -- **年度の期間の外に結び付き、エラーも警告も出ない**（2026-09-22 の自己レビューで実測）。
   AND date(m.start_date) BETWEEN date(y.start_date) AND date(y.end_date)
   AND NOT EXISTS (
        SELECT 1 FROM accounting_periods p
         WHERE p.fiscal_year_id = y.id AND date(p.start_date) = date(m.start_date));

-- 伝票番号の採番。**会計年度ごとに 1 行（I-17）。** 無いと第 17 期に計上できない。
INSERT INTO journal_entry_sequences (fiscal_year_id, next_entry_no)
SELECT y.id, 1
  FROM fiscal_years y
 WHERE y.code = 'FY17'
   AND NOT EXISTS (
        SELECT 1 FROM journal_entry_sequences s WHERE s.fiscal_year_id = y.id);
