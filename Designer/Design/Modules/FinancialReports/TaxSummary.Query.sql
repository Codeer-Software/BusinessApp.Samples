-- 消費税集計表（ADR-0052 で全面改訂）
--
-- 設計の要点:
-- 1) 金額は「符号を持たせて差引」で出す。journal_lines.amount は常に正の絶対値で、符号は dc にしか
--    無い。旧実装は dc を見ずに単純合計していたため、赤伝で打ち消しても両建てで積み上がっていた
--    （改善候補 B-6）。符号の基準は勘定科目の正残側 accounts.dc_normal —— 税区分から「自然な側」を
--    決める案もあったが、不課税・対象外の科目に自然な側が定義できないため科目基準にした。
--    市販ソフトが科目の正残側で符号を決めているのと同じ考え方。
-- 2) 逆側に立った金額を「戻し」列で見せる。赤黒訂正と売上返品・値引を DB 上で区別する情報が無いため
--    差引に溶かし込むだけにすると、申告で別掲すべき「対価の返還等」の存在が誰にも見えなくなる。
--    差引と戻しの両方を出して、別掲の要否は人が判断できるようにする。
-- 3) インボイス経過措置は取引日で控除割合を解決し、**割合ごとに行を分ける**。
--    令和8年度税制改正で 80→70→50→30→0% の5段階になり、80%→70% の改定日が 2026-10-01。
--    第18期（2026-04-01〜2027-03-31）はこの改定日をまたぐので、年度合計に単一の割合を掛けると誤る。
-- 4) 末尾に課税売上割合と控除方式の判定を出す（合計残高試算表の「合計（貸借検算）」と同じ
--    UNION ALL + sort_key で最終行にする方式）。判定閾値は system_thresholds（ddl/510）。
--
-- 税区分未設定（NULL）の行は存在しない前提（ADR-0052。490 で移行し 500 で NOT NULL 化）。
--
-- 【期間の解決】(BUG-0284) 集計範囲は **必ず日付で閉じる**。旧実装は期間が空のとき日付条件を
--   一切付けず、journal_entries.fiscal_year_id（非正規化列）だけを頼りにしていた。この列が
--   entry_date とずれた伝票が 1 件でもあると、その期の納付税額の顔をした複数年度の合算が出る。
--   税額はそのまま申告の数字になるので、日付で閉じる方を正とする
--   （TrialBalance / GeneralLedger / CashBook と同じ「SQL 側で当年度へフォールバックする」流儀）。
-- 対象年度は @fiscal_year_id（明示選択）→ 入っている方の日付 → 今日、の順で解決する。
-- 期間（自）（至）は年度の内側にクランプする（ヘッダの「期間指定は年度の内側を絞る」を厳密化。
--   年度だけ選んで（至）に翌年度の日付を入れても年度をはみ出さない）。
-- 年度が 1 件も見つからないときは全期間へ縮退する（TrialBalance と同じ。空表より全件の方が異常に気づける）。
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
    tc.id                          AS tc_id,
    tc.display_order               AS display_order,
    tc.name                        AS tc_name,
    tc.taxation_type               AS taxation_type,
    l.is_tax_line                  AS is_tax_line,
    l.amount                       AS amount,
    -- 科目の正残側に立っていれば +、逆側（赤伝・返品・値引）なら −
    CASE WHEN l.dc = a.dc_normal THEN 1 ELSE -1 END AS sgn,
    -- 経過措置の控除割合は取引日で期間解決する。経過措置でない課税仕入は 100%
    CASE WHEN tc.uses_transition_deduction = 1
         THEN COALESCE((SELECT r.rate_percent FROM invoice_transition_rates r
                         WHERE date(e.entry_date) >= date(r.valid_from)
                           AND date(e.entry_date) <= date(r.valid_to)), 0)
         ELSE 100
    END AS deduct_rate
  FROM journal_lines l
  JOIN journal_entries e ON e.id  = l.journal_entry_id
  JOIN tax_categories tc ON tc.id = l.tax_category_id
  JOIN accounts a        ON a.id  = l.account_id
  WHERE e.status = 'posted'
    -- 期間指定は年度の内側を絞る（中間申告・月次の税額把握用。未指定なら年度まるごと＝rng が解決済み）
    AND date(e.entry_date) >= (SELECT d_from FROM rng)
    AND date(e.entry_date) <= (SELECT d_to   FROM rng)
),
agg AS (
  SELECT
    tc_id, display_order, tc_name, taxation_type, deduct_rate,
    SUM(CASE WHEN is_tax_line = 0 THEN amount * sgn ELSE 0 END)        AS base_amount,
    SUM(CASE WHEN is_tax_line = 1 THEN amount * sgn ELSE 0 END)        AS tax_amount,
    SUM(CASE WHEN is_tax_line = 0 AND sgn = -1 THEN amount ELSE 0 END) AS base_reverse,
    SUM(CASE WHEN is_tax_line = 1 AND sgn = -1 THEN amount ELSE 0 END) AS tax_reverse
  FROM lines
  GROUP BY tc_id, deduct_rate
),
sales AS (
  -- 課税売上割合 = (課税売上 + 免税売上) ÷ (課税売上 + 免税売上 + 非課税売上)
  -- 不課税・対象外は分母に含めない（国税庁 タックスアンサー No.6405）
  SELECT
    COALESCE(SUM(CASE WHEN taxation_type IN ('taxable_sales', 'export_exempt') THEN base_amount ELSE 0 END), 0) AS taxable,
    COALESCE(SUM(CASE WHEN taxation_type = 'exempt_sales' THEN base_amount ELSE 0 END), 0)                      AS tax_exempt
  FROM agg
),
th AS (
  SELECT
    (SELECT amount FROM v_system_threshold_current WHERE code = 'FULL_DEDUCT_RATIO_MIN') AS ratio_min,
    (SELECT amount FROM v_system_threshold_current WHERE code = 'FULL_DEDUCT_SALES_CAP') AS sales_cap
)
SELECT * FROM (
  SELECT
    -- 経過措置で行が割れるときは控除割合の大きい順に並べる（80% → 70%）
    agg.display_order * 10 + (100 - agg.deduct_rate) / 10 AS sort_key,
    agg.tc_name AS tax_category_name,
    CASE agg.taxation_type
      WHEN 'taxable_sales'    THEN '課税売上'
      WHEN 'taxable_purchase' THEN '課税仕入'
      WHEN 'exempt_sales'     THEN '非課税売上'
      WHEN 'exempt_purchase'  THEN '非課税仕入'
      WHEN 'non_taxable'      THEN '不課税'
      WHEN 'export_exempt'    THEN '免税売上'
      ELSE '対象外'
    END AS taxation_type_name,
    agg.base_amount + agg.tax_amount AS gross_amount,
    agg.base_amount                  AS base_amount,
    agg.tax_amount                   AS tax_amount,
    agg.base_reverse                 AS base_reverse,
    agg.tax_reverse                  AS tax_reverse,
    -- 控除率・控除対象税額は課税仕入だけに出す（売上・非課税仕入・不課税・対象外は空欄）。
    -- 控除率は**その区分がどの経過措置期間に当たるかを示す情報**であって、ここで掛ける係数ではない。
    -- 【重要・BUG-0411】控除対象税額は「計上済みの仮払消費税」そのもの。
    --   経過措置の 80%（70%…）は**仕訳を起こす時点で既に適用済み**で、控除できない残りは
    --   本体（費用）に算入されている（税抜経理・国税庁の「取引時に処理する方法」）。
    --   例: 免税事業者から 11,000 円の仕入 → 仮払消費税 800 ／ 修繕費 10,200。
    --   したがって申告の控除対象仕入税額は **800**。ここで再度 80% を掛けると 640 になり、
    --   **控除額を 20% 過小に出す＝納めすぎになる**。旧実装はこれをやっていた。
    CASE WHEN agg.taxation_type = 'taxable_purchase' THEN agg.deduct_rate || '%' END AS deduct_rate,
    CASE WHEN agg.taxation_type = 'taxable_purchase'
         THEN agg.tax_amount END AS deductible_tax
  FROM agg

  UNION ALL

  -- 最終行: 課税売上割合と控除方式の判定
  SELECT
    999999 AS sort_key,
    '課税売上割合（表示期間）' AS tax_category_name,
    -- 表示は切り捨て。四捨五入だと 99.995% が「100.0%」になり、非課税売上があるのに
    -- 全額が課税売上に見えてしまう（実測。受取利息 620 円で発生した）
    CASE WHEN (s.taxable + s.tax_exempt) = 0 THEN '売上がありません'
         ELSE printf('%.2f%%', CAST(s.taxable * 10000.0 / (s.taxable + s.tax_exempt) AS INTEGER) / 100.0)
              || '（'
              -- **閾値が引けないときは断定しない**（BUG-0119）。
              -- `th` は制度閾値マスタからスカラサブクエリで引くので、行が無い／有効期間が切れていると
              -- `ratio_min` / `sales_cap` が NULL になる。SQL の三値論理では `x >= NULL` は真にならないので
              -- 旧実装は**必ず ELSE 側に落ち、課税売上割合 100% の会社にも「個別対応方式が必要です」と
              -- 断定して見せていた**。エラーにも警告にもならないので、閾値を消したことが原因だと気づけない
              || CASE WHEN th.ratio_min IS NULL OR th.sales_cap IS NULL
                      THEN '控除方式は判定できません——制度閾値 FULL_DEDUCT_RATIO_MIN / FULL_DEDUCT_SALES_CAP が'
                           || '表示期間に有効ではありません。業務マスタ > 税制 > 制度閾値 を確認してください'
                      WHEN s.taxable * 100.0 / (s.taxable + s.tax_exempt) >= th.ratio_min
                       AND s.taxable <= th.sales_cap
                      THEN '全額控除できます'
                      ELSE '個別対応方式または一括比例配分方式が必要です'
                 END
              || '）'
    END AS taxation_type_name,
    NULL AS gross_amount,
    NULL AS base_amount,
    NULL AS tax_amount,
    NULL AS base_reverse,
    NULL AS tax_reverse,
    CAST(NULL AS TEXT) AS deduct_rate,
    NULL AS deductible_tax
  FROM sales s CROSS JOIN th
)
ORDER BY sort_key
