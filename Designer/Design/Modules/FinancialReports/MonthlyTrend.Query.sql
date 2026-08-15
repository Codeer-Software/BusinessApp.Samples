-- 月次推移表（D-1）: PL=月次発生額の12ヶ月横並び / BS=月末残高の12ヶ月横並び
-- @fiscal_year_id: 対象年度（NULL=現在日付を含む年度）
-- @statement: 'PL'（既定）/ 'BS'
-- 先頭行（sort_key '00-…'）は暦月ヘッダ（第n月が実際の何月かを示す。決算期変更に自動追随）
WITH yr AS (
  SELECT id FROM fiscal_years
  WHERE id = COALESCE(@fiscal_year_id,
    (SELECT id FROM fiscal_years
     WHERE date(start_date) <= date('now', 'localtime') AND date(end_date) >= date('now', 'localtime')))
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
mv AS (
  SELECT l.account_id, p.period_no,
         SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  JOIN per p ON date(e.entry_date) BETWEEN date(p.start_date) AND date(p.end_date)
  WHERE e.status = 'posted'
    AND e.fiscal_year_id IN (SELECT id FROM yr)
  GROUP BY l.account_id, p.period_no
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
  SUM(CASE WHEN period_no = 12 THEN cal_month END) AS m12,
  NULL AS total
FROM per

UNION ALL
-- PL 科目行
SELECT printf('%02d', r.section_order) || '-1-' || r.code, r.cat_name, r.name,
  SUM(CASE WHEN r.period_no = 1  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 2  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 3  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 4  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 5  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 6  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 7  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 8  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 9  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 10 THEN r.amt END),
  SUM(CASE WHEN r.period_no = 11 THEN r.amt END),
  SUM(CASE WHEN r.period_no = 12 THEN r.amt END),
  SUM(r.amt)
FROM plrow r
WHERE (SELECT s FROM stmt) = 'PL'
GROUP BY r.section_order, r.code, r.name, r.cat_name

UNION ALL
-- PL 区分小計
SELECT printf('%02d', r.section_order) || '-2-ZZZZ', r.cat_name, r.cat_name || ' 計',
  SUM(CASE WHEN r.period_no = 1  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 2  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 3  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 4  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 5  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 6  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 7  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 8  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 9  THEN r.amt END),
  SUM(CASE WHEN r.period_no = 10 THEN r.amt END),
  SUM(CASE WHEN r.period_no = 11 THEN r.amt END),
  SUM(CASE WHEN r.period_no = 12 THEN r.amt END),
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
  SUM(CASE WHEN period_no = 12 THEN rev - cogs END),
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
  SUM(CASE WHEN period_no = 12 THEN rev - cogs - sga END),
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
  SUM(CASE WHEN period_no = 12 THEN rev - cogs - sga + noi - noe END),
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
  SUM(CASE WHEN period_no = 12 THEN rev - cogs - sga + noi - noe + ei - el END),
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
  SUM(CASE WHEN period_no = 12 THEN rev - cogs - sga + noi - noe + ei - el - tax END),
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
  SUM(CASE WHEN b.period_no = 12 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 12 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END)
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
  SUM(CASE WHEN b.period_no = 12 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END),
  SUM(CASE WHEN b.period_no = 12 THEN CASE WHEN b.account_type = 'asset' THEN b.dmc ELSE -b.dmc END END)
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
  SUM(CASE WHEN period_no = 12 THEN ast END),
  SUM(CASE WHEN period_no = 12 THEN ast END)
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
  SUM(CASE WHEN period_no = 12 THEN lia END),
  SUM(CASE WHEN period_no = 12 THEN lia END)
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
  SUM(CASE WHEN period_no = 12 THEN ni END),
  SUM(CASE WHEN period_no = 12 THEN ni END)
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
  SUM(CASE WHEN period_no = 12 THEN eq + ni END),
  SUM(CASE WHEN period_no = 12 THEN eq + ni END)
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
  SUM(CASE WHEN period_no = 12 THEN lia + eq + ni END),
  SUM(CASE WHEN period_no = 12 THEN lia + eq + ni END)
FROM bsvn HAVING (SELECT s FROM stmt) = 'BS'

ORDER BY sort_key
