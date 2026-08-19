-- 月次推移表（D-1）: PL=月次発生額の12ヶ月横並び / BS=月末残高の12ヶ月横並び
-- @fiscal_year_id: 対象年度（NULL=現在日付を含む年度）
-- @statement: 'PL'（既定）/ 'BS'
-- 先頭行（sort_key '00-…'）は暦月ヘッダ（第n月が実際の何月かを示す。決算期変更に自動追随）
WITH yr AS (
  SELECT id FROM fiscal_years
  WHERE id = COALESCE(@fiscal_year_id,
    (SELECT id FROM (
       -- 【今日がどの年度にも入らない日の縮退・BUG-0288】
       --   旧実装は「今日を含む年度」だけを見ていたので、期末をまたいで年度マスタの登録が遅れると
       --   `= NULL` になり、**帳票がある日突然すべて空になる**（エラーも警告も出ない）。
       --   年度登録は年 1 回の作業なので現実に起きる。**直近の年度へ縮退する**。
       --   優先順は入出金起票の残高（BUG-0097）と同じ:
       --     ①今日を含む年度 →②直前に終わった年度 →③これから始まる年度
       SELECT id,
              CASE WHEN date(start_date) <= date('now','localtime')
                    AND date(end_date)   >= date('now','localtime') THEN 0
                   WHEN date(end_date)   <  date('now','localtime') THEN 1
                   ELSE 2 END AS pri,
              CASE WHEN date(end_date) < date('now','localtime')
                   THEN julianday(date('now','localtime')) - julianday(date(end_date))
                   ELSE julianday(date(start_date)) - julianday(date('now','localtime')) END AS ord
       FROM fiscal_years
       ORDER BY pri, ord
       LIMIT 1)))
),
stmt AS (
  SELECT COALESCE(@statement, 'PL') AS s
),
per AS (
  SELECT period_no,
         CAST(strftime('%m', start_date) AS INTEGER) AS cal_month,
         start_date, end_date
  FROM fiscal_periods
  WHERE fiscal_year_id IN (SELECT id FROM yr)
),
-- 【変則決算期に耐える・BUG-0111】
--   画面の月列は m01〜m12 の **12 本固定**（CLB のフィールドは可変にできない）。ところが
--   `fiscal_periods` に 12 本という制約は無く、決算期変更に伴う 13〜18 ヶ月の変則決算期がありうる。
--   旧実装は月列も期末列も `period_no = 12` の決め打ちで、
--     ・PL は第 13 月以降が Total には入るが列には出ない → **12 列を足しても Total に合わない**
--     ・BS は「期末残高」ではなく「第 12 月末残高」を出す（列名は「合計/期末」なのに）
--   という壊れ方をした。
--   対処は **PL は 12 列目に第 12 月以降を畳む／BS は最終期間の残高を出す**。
--   12 列に収まらないことは画面側（`MonthlyTrend.mod.cs`）が警告する
lastp AS (
  SELECT COALESCE(MAX(period_no), 12) AS n FROM per
),
-- 【期間に載らない仕訳を落とさない・BUG-0110】
--   対象は他 6 帳票と同じく `e.fiscal_year_id`。期間の JOIN は**月列への割り当てだけ**に使う。
--   旧実装は INNER JOIN だったため、`fiscal_periods` の隙間に落ちた日付の仕訳が
--   **月次推移表からだけ静かに消え**、PL の年間合計 ≠ 月次推移の Total、BS の検算行が合わなくなった
--   （`journal_entries` には entry_date と fiscal_year_id の整合制約が無く、
--     `fiscal_periods` が年度を隙間なく覆う保証も無い）。
--   割り当てできない仕訳は、期首より前なら最初の期間へ、期末より後なら最後の期間へ寄せる。
--   こうすれば「列の合計 ＝ Total」が常に成り立ち、異常な日付の伝票も画面から見える。
mv AS (
  SELECT l.account_id,
         COALESCE(p.period_no,
                  CASE WHEN date(e.entry_date) < (SELECT MIN(date(start_date)) FROM per)
                       THEN (SELECT MIN(period_no) FROM per)
                       ELSE (SELECT MAX(period_no) FROM per) END,
                  1) AS period_no,
         SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  LEFT JOIN per p ON date(e.entry_date) BETWEEN date(p.start_date) AND date(p.end_date)
  WHERE e.status = 'posted'
    AND e.fiscal_year_id IN (SELECT id FROM yr)
  GROUP BY l.account_id, 2
),

-- ============ PL（月次発生額） ============
plrow AS (
  SELECT a.code, a.name,
         c.code AS cat_code, c.name AS cat_name, c.section_order,
         m.period_no,
         CASE WHEN a.account_type = 'revenue' THEN -m.dmc ELSE m.dmc END AS amt
  FROM mv m
  JOIN accounts a ON a.id = m.account_id
  JOIN account_categories c ON c.id = a.category_id
  WHERE c.statement = 'PL'
),
plm AS (
  SELECT p.period_no,
    COALESCE(SUM(CASE WHEN r.cat_code = 'REV'  THEN r.amt END), 0) AS rev,
    COALESCE(SUM(CASE WHEN r.cat_code = 'COGS' THEN r.amt END), 0) AS cogs,
    COALESCE(SUM(CASE WHEN r.cat_code = 'SGA'  THEN r.amt END), 0) AS sga,
    COALESCE(SUM(CASE WHEN r.cat_code = 'NOI'  THEN r.amt END), 0) AS noi,
    COALESCE(SUM(CASE WHEN r.cat_code = 'NOE'  THEN r.amt END), 0) AS noe,
    COALESCE(SUM(CASE WHEN r.cat_code = 'EI'   THEN r.amt END), 0) AS ei,
    COALESCE(SUM(CASE WHEN r.cat_code = 'EL'   THEN r.amt END), 0) AS el,
    COALESCE(SUM(CASE WHEN r.cat_code = 'TAX'  THEN r.amt END), 0) AS tax
  FROM per p
  LEFT JOIN plrow r ON r.period_no = p.period_no
  GROUP BY p.period_no
),

-- ============ BS（月末残高＝期首＋累計増減） ============
ob AS (
  SELECT account_id, SUM(balance) AS bal
  FROM opening_balances
  WHERE fiscal_year_id IN (SELECT id FROM yr)
  GROUP BY account_id
),
bsacct AS (
  SELECT a.id, a.code, a.name, a.account_type,
         c.name AS cat_name, c.section_order
  FROM accounts a
  JOIN account_categories c ON c.id = a.category_id
  WHERE c.statement = 'BS'
    AND ( COALESCE((SELECT bal FROM ob WHERE account_id = a.id), 0) <> 0
       OR EXISTS (SELECT 1 FROM mv WHERE account_id = a.id) )
),
bscum AS (
  SELECT b.id, b.code, b.name, b.account_type, b.cat_name, b.section_order,
         p.period_no,
         COALESCE((SELECT bal FROM ob WHERE account_id = b.id), 0)
         + COALESCE((SELECT SUM(m.dmc) FROM mv m
                     WHERE m.account_id = b.id AND m.period_no <= p.period_no), 0) AS dmc
  FROM bsacct b
  CROSS JOIN per p
),
nim AS (
  SELECT p.period_no,
    COALESCE(-(SELECT SUM(m.dmc)
               FROM mv m
               JOIN accounts a ON a.id = m.account_id
               JOIN account_categories c ON c.id = a.category_id
               WHERE c.statement = 'PL' AND m.period_no <= p.period_no), 0) AS ni
  FROM per p
),
bsv AS (
  SELECT b.period_no,
    COALESCE(SUM(CASE WHEN b.account_type = 'asset'     THEN  b.dmc END), 0) AS ast,
    COALESCE(SUM(CASE WHEN b.account_type = 'liability' THEN -b.dmc END), 0) AS lia,
    COALESCE(SUM(CASE WHEN b.account_type = 'equity'    THEN -b.dmc END), 0) AS eq
  FROM bscum b
  GROUP BY b.period_no
),
bsvn AS (
  SELECT v.period_no, v.ast, v.lia, v.eq, n.ni
  FROM bsv v JOIN nim n ON n.period_no = v.period_no
)

-- ============ 出力 ============
-- 暦月ヘッダ行（PL/BS 共通）
SELECT '00-0-0000' AS sort_key, '' AS section, '月（暦月）' AS item,
  SUM(CASE WHEN period_no = 1  THEN cal_month END) AS m01,
  SUM(CASE WHEN period_no = 2  THEN cal_month END) AS m02,
  SUM(CASE WHEN period_no = 3  THEN cal_month END) AS m03,
  SUM(CASE WHEN period_no = 4  THEN cal_month END) AS m04,
  SUM(CASE WHEN period_no = 5  THEN cal_month END) AS m05,
  SUM(CASE WHEN period_no = 6  THEN cal_month END) AS m06,
  SUM(CASE WHEN period_no = 7  THEN cal_month END) AS m07,
  SUM(CASE WHEN period_no = 8  THEN cal_month END) AS m08,
  SUM(CASE WHEN period_no = 9  THEN cal_month END) AS m09,
  SUM(CASE WHEN period_no = 10 THEN cal_month END) AS m10,
  SUM(CASE WHEN period_no = 11 THEN cal_month END) AS m11,
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN cal_month END) AS m12,
  NULL AS total
FROM per

UNION ALL
-- PL 科目行
--
-- **月セルは 0 埋めする**（BUG-0114）。`plrow` はその月に取引のある科目 × 期間しか行を持たないので、
-- 素の `SUM(CASE WHEN … END)` は取引の無い月が NULL＝空白セルになる。
-- 一方、段階利益行（売上総利益〜当期純利益）は `plm` を通り、`plm` は `per LEFT JOIN plrow` ＋
-- `COALESCE(…, 0)` で全期間を 0 埋めしているので **0 と表示される**。
-- 結果、**同じ表の中で「空白」と「0」が混ざり、意味の違いがあるように見えてしまう**。
-- 月次推移表で 0 と空白に意味の差は無い（どちらも「その月に動きが無い」）ので、**0 に揃える**
SELECT printf('%02d', r.section_order) || '-1-' || r.code, r.cat_name, r.name,
  COALESCE(SUM(CASE WHEN r.period_no = 1  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 2  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 3  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 4  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 5  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 6  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 7  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 8  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 9  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 10 THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 11 THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no >= 12 THEN r.amt END), 0),
  SUM(r.amt)
FROM plrow r
WHERE (SELECT s FROM stmt) = 'PL'
GROUP BY r.section_order, r.code, r.name, r.cat_name

UNION ALL
-- PL 区分小計
SELECT printf('%02d', r.section_order) || '-2-ZZZZ', r.cat_name, r.cat_name || ' 計',
  COALESCE(SUM(CASE WHEN r.period_no = 1  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 2  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 3  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 4  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 5  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 6  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 7  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 8  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 9  THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 10 THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no = 11 THEN r.amt END), 0),
  COALESCE(SUM(CASE WHEN r.period_no >= 12 THEN r.amt END), 0),
  SUM(r.amt)
FROM plrow r
WHERE (SELECT s FROM stmt) = 'PL'
GROUP BY r.section_order, r.cat_name

UNION ALL
-- PL 段階利益: 売上総利益
SELECT '51-9-ZZZZ', '段階利益', '売上総利益',
  SUM(CASE WHEN period_no = 1  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 2  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 3  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 4  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 5  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 6  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 7  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 8  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 9  THEN rev - cogs END),
  SUM(CASE WHEN period_no = 10 THEN rev - cogs END),
  SUM(CASE WHEN period_no = 11 THEN rev - cogs END),
  SUM(CASE WHEN period_no >= 12 THEN rev - cogs END),
  SUM(rev - cogs)
FROM plm HAVING (SELECT s FROM stmt) = 'PL'

UNION ALL
-- PL 段階利益: 営業利益
SELECT '52-9-ZZZZ', '段階利益', '営業利益',
  SUM(CASE WHEN period_no = 1  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 2  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 3  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 4  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 5  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 6  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 7  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 8  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 9  THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 10 THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no = 11 THEN rev - cogs - sga END),
  SUM(CASE WHEN period_no >= 12 THEN rev - cogs - sga END),
  SUM(rev - cogs - sga)
FROM plm HAVING (SELECT s FROM stmt) = 'PL'

UNION ALL
-- PL 段階利益: 経常利益
SELECT '54-9-ZZZZ', '段階利益', '経常利益',
  SUM(CASE WHEN period_no = 1  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 2  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 3  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 4  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 5  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 6  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 7  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 8  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 9  THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 10 THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no = 11 THEN rev - cogs - sga + noi - noe END),
  SUM(CASE WHEN period_no >= 12 THEN rev - cogs - sga + noi - noe END),
  SUM(rev - cogs - sga + noi - noe)
FROM plm HAVING (SELECT s FROM stmt) = 'PL'

UNION ALL
-- PL 段階利益: 税引前当期純利益
SELECT '56-9-ZZZZ', '段階利益', '税引前当期純利益',
  SUM(CASE WHEN period_no = 1  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 2  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 3  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 4  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 5  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 6  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 7  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 8  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 9  THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 10 THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no = 11 THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(CASE WHEN period_no >= 12 THEN rev - cogs - sga + noi - noe + ei - el END),
  SUM(rev - cogs - sga + noi - noe + ei - el)
FROM plm HAVING (SELECT s FROM stmt) = 'PL'

UNION ALL
-- PL 段階利益: 当期純利益
SELECT '57-9-ZZZZ', '段階利益', '当期純利益',
  SUM(CASE WHEN period_no = 1  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 2  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 3  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 4  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 5  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 6  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 7  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 8  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 9  THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 10 THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no = 11 THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(CASE WHEN period_no >= 12 THEN rev - cogs - sga + noi - noe + ei - el - tax END),
  SUM(rev - cogs - sga + noi - noe + ei - el - tax)
FROM plm HAVING (SELECT s FROM stmt) = 'PL'

UNION ALL
-- BS 科目行（月末残高）
SELECT printf('%02d', b.section_order) || '-1-' || b.code, b.cat_name, b.name,
  SUM(CASE WHEN b.period_no = 1  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 2  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 3  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 4  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 5  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 6  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 7  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 8  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 9  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 10 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 11 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = (SELECT n FROM lastp) THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = (SELECT n FROM lastp) THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END)
FROM bscum b
WHERE (SELECT s FROM stmt) = 'BS'
GROUP BY b.section_order, b.code, b.name, b.cat_name

UNION ALL
-- BS 区分小計（月末残高）
SELECT printf('%02d', b.section_order) || '-2-ZZZZ', b.cat_name, b.cat_name || ' 計',
  SUM(CASE WHEN b.period_no = 1  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 2  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 3  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 4  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 5  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 6  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 7  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 8  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 9  THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 10 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 11 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = (SELECT n FROM lastp) THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = (SELECT n FROM lastp) THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END)
FROM bscum b
WHERE (SELECT s FROM stmt) = 'BS'
GROUP BY b.section_order, b.cat_name

UNION ALL
-- BS 資産合計
SELECT '29-9-ZZZZ', '資産', '資産合計',
  SUM(CASE WHEN period_no = 1  THEN ast END),
  SUM(CASE WHEN period_no = 2  THEN ast END),
  SUM(CASE WHEN period_no = 3  THEN ast END),
  SUM(CASE WHEN period_no = 4  THEN ast END),
  SUM(CASE WHEN period_no = 5  THEN ast END),
  SUM(CASE WHEN period_no = 6  THEN ast END),
  SUM(CASE WHEN period_no = 7  THEN ast END),
  SUM(CASE WHEN period_no = 8  THEN ast END),
  SUM(CASE WHEN period_no = 9  THEN ast END),
  SUM(CASE WHEN period_no = 10 THEN ast END),
  SUM(CASE WHEN period_no = 11 THEN ast END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN ast END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN ast END)
FROM bsvn HAVING (SELECT s FROM stmt) = 'BS'

UNION ALL
-- BS 負債合計
SELECT '39-9-ZZZZ', '負債', '負債合計',
  SUM(CASE WHEN period_no = 1  THEN lia END),
  SUM(CASE WHEN period_no = 2  THEN lia END),
  SUM(CASE WHEN period_no = 3  THEN lia END),
  SUM(CASE WHEN period_no = 4  THEN lia END),
  SUM(CASE WHEN period_no = 5  THEN lia END),
  SUM(CASE WHEN period_no = 6  THEN lia END),
  SUM(CASE WHEN period_no = 7  THEN lia END),
  SUM(CASE WHEN period_no = 8  THEN lia END),
  SUM(CASE WHEN period_no = 9  THEN lia END),
  SUM(CASE WHEN period_no = 10 THEN lia END),
  SUM(CASE WHEN period_no = 11 THEN lia END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN lia END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN lia END)
FROM bsvn HAVING (SELECT s FROM stmt) = 'BS'

UNION ALL
-- BS 当期純利益（累計）
SELECT '48-1-ZZZZ', '純資産', '当期純利益',
  SUM(CASE WHEN period_no = 1  THEN ni END),
  SUM(CASE WHEN period_no = 2  THEN ni END),
  SUM(CASE WHEN period_no = 3  THEN ni END),
  SUM(CASE WHEN period_no = 4  THEN ni END),
  SUM(CASE WHEN period_no = 5  THEN ni END),
  SUM(CASE WHEN period_no = 6  THEN ni END),
  SUM(CASE WHEN period_no = 7  THEN ni END),
  SUM(CASE WHEN period_no = 8  THEN ni END),
  SUM(CASE WHEN period_no = 9  THEN ni END),
  SUM(CASE WHEN period_no = 10 THEN ni END),
  SUM(CASE WHEN period_no = 11 THEN ni END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN ni END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN ni END)
FROM bsvn HAVING (SELECT s FROM stmt) = 'BS'

UNION ALL
-- BS 純資産合計（当期純利益込み）
SELECT '48-9-ZZZZ', '純資産', '純資産合計',
  SUM(CASE WHEN period_no = 1  THEN eq + ni END),
  SUM(CASE WHEN period_no = 2  THEN eq + ni END),
  SUM(CASE WHEN period_no = 3  THEN eq + ni END),
  SUM(CASE WHEN period_no = 4  THEN eq + ni END),
  SUM(CASE WHEN period_no = 5  THEN eq + ni END),
  SUM(CASE WHEN period_no = 6  THEN eq + ni END),
  SUM(CASE WHEN period_no = 7  THEN eq + ni END),
  SUM(CASE WHEN period_no = 8  THEN eq + ni END),
  SUM(CASE WHEN period_no = 9  THEN eq + ni END),
  SUM(CASE WHEN period_no = 10 THEN eq + ni END),
  SUM(CASE WHEN period_no = 11 THEN eq + ni END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN eq + ni END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN eq + ni END)
FROM bsvn HAVING (SELECT s FROM stmt) = 'BS'

UNION ALL
-- BS 検算: 負債・純資産合計（資産合計と一致すべき）
SELECT '59-9-ZZZZ', '検算', '負債・純資産合計',
  SUM(CASE WHEN period_no = 1  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 2  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 3  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 4  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 5  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 6  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 7  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 8  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 9  THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 10 THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 11 THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = (SELECT n FROM lastp) THEN lia + eq + ni END)
FROM bsvn HAVING (SELECT s FROM stmt) = 'BS'

ORDER BY sort_key
