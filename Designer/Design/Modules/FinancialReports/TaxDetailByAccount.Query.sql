-- 科目別税区分表（ADR-0052 の続き・2026-08-12）
--
-- 消費税集計表が「税区分ごとの合計」を出すのに対し、こちらは **科目 × 税区分** の内訳を出す。
-- 目的は消費税集計表と違い、申告書への転記ではなく **数字の検証**:
--   ・「課税仕入 10% が前期比で多い。内訳は？」に答える
--   ・**税区分の誤設定を見つける**——給与に課税仕入が付いていないか、海外 SaaS の
--     利用料が対象外になって控除漏れになっていないか、など
-- 実務の消費税の誤りは計算ミスより「税区分の付け間違い」が多く、合計表だけでは発見できない。
-- 税区分で「対象外」「不課税」に絞り、そこに損益科目が並んでいないかを見るのが定石の使い方。
--
-- 並びは科目コード順 → 税区分順。**1 つの科目に複数の税区分が縦に並ぶ**ので、
-- 「この科目には本来どの税区分が付くはずか」を目で追える向きにしている。
-- 金額の考え方（符号は accounts.dc_normal 基準の差引・逆側は「戻し」列）は消費税集計表と同じ。
--
-- 【期間の解決】(BUG-0284) 消費税集計表（TaxSummary.Query.sql）と同一。集計範囲は必ず日付で閉じる。
--   旧実装は期間が空のとき日付条件を付けず、非正規化列 journal_entries.fiscal_year_id だけを頼りに
--   していたため、この列が entry_date とずれた伝票があると複数年度が合算された。
--   対象年度は @fiscal_year_id → 入っている方の日付 → 今日 の順で解決し、期間は年度の内側へクランプ、
--   年度が見つからないときは全期間へ縮退する（TrialBalance と同じ流儀）。
WITH fy AS (
  SELECT start_date, end_date FROM fiscal_years
  WHERE (@fiscal_year_id IS NOT NULL AND id = @fiscal_year_id)
     OR (@fiscal_year_id IS NULL
         AND date(start_date) <= date(COALESCE(@date_from, @date_to, date('now', 'localtime')))
         AND date(end_date)   >= date(COALESCE(@date_from, @date_to, date('now', 'localtime'))))
),
rng AS (
  SELECT
    MAX(COALESCE(date(@date_from), '0001-01-01'),
        COALESCE((SELECT date(start_date) FROM fy), '0001-01-01')) AS d_from,
    MIN(COALESCE(date(@date_to),   '9999-12-31'),
        COALESCE((SELECT date(end_date)   FROM fy), '9999-12-31')) AS d_to
),
lines AS (
  SELECT
    a.id            AS account_id,
    a.code          AS account_code,
    a.name          AS account_name,
    tc.id           AS tc_id,
    tc.display_order AS tc_order,
    tc.name         AS tc_name,
    tc.taxation_type AS taxation_type,
    l.is_tax_line   AS is_tax_line,
    l.amount        AS amount,
    CASE WHEN l.dc = a.dc_normal THEN 1 ELSE -1 END AS sgn
  FROM journal_lines l
  JOIN journal_entries e ON e.id  = l.journal_entry_id
  JOIN tax_categories tc ON tc.id = l.tax_category_id
  -- **税行は「本体行の科目」に寄せる**（BUG-0115）。
  -- 消費税は `is_tax_line = 1` の別行として仮払消費税(1900) / 仮受消費税(2200) の科目に立つ（ADR-0052）。
  -- 素直に `l.account_id` で科目に割り当てると、本表は科目 × 税区分でグループ化するので
  -- **費用・収益科目の行には税行が 1 本も入らない**——「消費税額 0」「税込対価 ＝ 本体金額」になり、
  -- 税額だけが 1900 / 2200 の行に独立して並ぶ。列名が示す意味と中身が食い違う。
  -- 税行は `parent_line_no` で本体行を指しているので、**その本体行の科目に付け替える**。
  -- こうすると「旅費交通費 / 課税仕入 10%：本体 10,000・税 1,000・税込 11,000」と読める
  -- （税区分の付け間違いを探すという本表の目的にも、こちらのほうが合う）。
  -- 消費税集計表（TaxSummary）は税区分単位なのでもともと税行も同じ区分に入り、影響を受けない。
  -- `parent_line_no` が解決できない税行は自分の科目のまま残す（黙って消さない）
  LEFT JOIN journal_lines pl
         ON pl.journal_entry_id = l.journal_entry_id
        AND pl.line_no          = l.parent_line_no
        AND l.is_tax_line       = 1
  JOIN accounts a        ON a.id  = COALESCE(pl.account_id, l.account_id)
  WHERE e.status = 'posted'
    AND date(e.entry_date) >= (SELECT d_from FROM rng)
    AND date(e.entry_date) <= (SELECT d_to   FROM rng)
    AND (@tax_category_id IS NULL OR tc.id = @tax_category_id)
    AND (@account_id      IS NULL OR a.id  = @account_id)
)
SELECT
  CAST(account_code AS INTEGER) * 1000 + tc_order AS sort_key,
  account_code,
  account_name,
  tc_name AS tax_category_name,
  CASE taxation_type
    WHEN 'taxable_sales'    THEN '課税売上'
    WHEN 'taxable_purchase' THEN '課税仕入'
    WHEN 'exempt_sales'     THEN '非課税売上'
    WHEN 'exempt_purchase'  THEN '非課税仕入'
    WHEN 'non_taxable'      THEN '不課税'
    WHEN 'export_exempt'    THEN '免税売上'
    ELSE '対象外'
  END AS taxation_type_name,
  SUM(CASE WHEN is_tax_line = 0 THEN amount * sgn ELSE 0 END)
    + SUM(CASE WHEN is_tax_line = 1 THEN amount * sgn ELSE 0 END) AS gross_amount,
  SUM(CASE WHEN is_tax_line = 0 THEN amount * sgn ELSE 0 END)        AS base_amount,
  SUM(CASE WHEN is_tax_line = 1 THEN amount * sgn ELSE 0 END)        AS tax_amount,
  SUM(CASE WHEN is_tax_line = 0 AND sgn = -1 THEN amount ELSE 0 END) AS base_reverse,
  SUM(CASE WHEN is_tax_line = 1 AND sgn = -1 THEN amount ELSE 0 END) AS tax_reverse
FROM lines
GROUP BY account_id, tc_id
ORDER BY sort_key
