-- キャッシュ・フロー計算書（D-3・間接法・簡易）
-- @fiscal_year_id: 対象年度（NULL=現在日付を含む年度）
--
-- 設計メモ:
-- ・Δ=当期 posted 仕訳の増減のみ（期首 opening_balances は増減に含めない。年次繰越は
--   ADR-0006 の opening_balances 生成方式のため、損益振替仕訳が存在しない前提が成立）
-- ・現金及び現金同等物 = 科目コード 1000〜1099 帯（現金/小口現金/普通預金/定期預金。
--   簡易版として定期預金も含める。コード帯は docs/04 §2.1 の帯域設計に依存）
-- ・減価償却費 = 科目コード 6300 固定（直接法＝資産直接減額。6300 変更時は要修正）
-- ・繰越利益剰余金(EQR)の当期仕訳増減 = 配当等（損益振替仕訳を作らないため）
-- ・利息の受取額/支払額の組替（小計の下に別掲）は簡易版では省略（税引前利益に含む）
-- ・恒等式: 営業CF+投資CF+財務CF = Δ現預金 が全仕訳の複式性から常に成立する分解
--   （全BS科目を漏れなく重複なくいずれかの行に割り当てている）
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
mv AS (
  SELECT l.account_id,
         SUM(CASE WHEN l.dc = 'D' THEN l.amount ELSE -l.amount END) AS dmc
  FROM journal_lines l
  JOIN journal_entries e ON e.id = l.journal_entry_id
  WHERE e.status = 'posted'
    AND e.fiscal_year_id IN (SELECT id FROM yr)
  GROUP BY l.account_id
),
acc AS (
  SELECT a.id, a.code, c.code AS cat, c.statement, COALESCE(m.dmc, 0) AS dmc
  FROM accounts a
  JOIN account_categories c ON c.id = a.category_id
  LEFT JOIN mv m ON m.account_id = a.id
),
v AS (
  SELECT
    -- 税引前当期純利益（PL 全区分から法人税等を除く。貸方正）
    -(SELECT COALESCE(SUM(dmc),0) FROM acc WHERE statement = 'PL' AND cat <> 'TAX') AS pretax,
    -- 減価償却費（6300）
    (SELECT COALESCE(SUM(dmc),0) FROM acc WHERE code = '6300') AS dep,
    -- 売上債権 Δ（借方正）: 売掛金1100・未収入金1110
    (SELECT COALESCE(SUM(dmc),0) FROM acc WHERE code IN ('1100','1110')) AS d_ar,
    -- 棚卸資産 Δ: 仕掛品1200
    (SELECT COALESCE(SUM(dmc),0) FROM acc WHERE code = '1200') AS d_inv,
    -- その他の流動資産 Δ（現預金帯・売上債権・棚卸資産を除く CA 全部。貸倒引当金1950 含む）
    (SELECT COALESCE(SUM(dmc),0) FROM acc
      WHERE cat = 'CA' AND NOT (code BETWEEN '1000' AND '1099')
        AND code NOT IN ('1100','1110','1200')) AS d_oca,
    -- 仕入債務 Δ（貸方正）: 買掛金2000
    -(SELECT COALESCE(SUM(dmc),0) FROM acc WHERE code = '2000') AS d_ap,
    -- その他の流動負債 Δ（貸方正。買掛金・短期借入金2040・未払法人税等2220 を除く CL 全部）
    -(SELECT COALESCE(SUM(dmc),0) FROM acc
      WHERE cat = 'CL' AND code NOT IN ('2000','2040','2220')) AS d_ocl,
    -- 法人税等（PL・借方正）と未払法人税等 Δ（貸方正）→ 支払額 = 費用計上 − 未払増加
    (SELECT COALESCE(SUM(dmc),0) FROM acc WHERE cat = 'TAX') AS taxpl,
    -(SELECT COALESCE(SUM(dmc),0) FROM acc WHERE code = '2220') AS d_taxdue,
    -- 有形・無形固定資産 Δ（借方正）: 取得 − 減価償却（直接法）− 売却簿価
    (SELECT COALESCE(SUM(dmc),0) FROM acc WHERE cat IN ('FAT','FAI')) AS d_fa,
    -- 投資その他の資産 Δ（借方正）: 敷金保証金・長期前払費用
    (SELECT COALESCE(SUM(dmc),0) FROM acc WHERE cat = 'FAO') AS d_fao,
    -- 財務（すべて貸方正）
    -(SELECT COALESCE(SUM(dmc),0) FROM acc WHERE code = '2040') AS d_sloan,
    -(SELECT COALESCE(SUM(dmc),0) FROM acc WHERE cat = 'LL') AS d_lloan,
    -(SELECT COALESCE(SUM(dmc),0) FROM acc WHERE cat IN ('EQC','EQS')) AS d_cap,
    -(SELECT COALESCE(SUM(dmc),0) FROM acc WHERE cat = 'EQR') AS d_ret,
    -- 現金及び現金同等物の期首残高（opening_balances は借方正）
    (SELECT COALESCE(SUM(ob.balance),0)
       FROM opening_balances ob JOIN accounts a2 ON a2.id = ob.account_id
      WHERE ob.fiscal_year_id IN (SELECT id FROM yr)
        AND a2.code BETWEEN '1000' AND '1099') AS cash_open
),
ln AS (
  SELECT
    pretax,
    dep,
    -d_ar  AS ar,
    -d_inv AS inv,
    -d_oca AS oca,
    d_ap   AS ap,
    d_ocl  AS ocl,
    -(taxpl - d_taxdue) AS taxpaid,
    -(d_fa + dep) AS fa_cf,
    -d_fao AS fao_cf,
    d_sloan AS sloan,
    d_lloan AS lloan,
    d_cap  AS cap,
    d_ret  AS ret,
    cash_open
  FROM v
),
tot AS (
  SELECT *,
    pretax + dep + ar + inv + oca + ap + ocl            AS op_sub,
    pretax + dep + ar + inv + oca + ap + ocl + taxpaid  AS op_cf,
    fa_cf + fao_cf                                      AS inv_cf,
    sloan + lloan + cap + ret                           AS fin_cf
  FROM ln
)

SELECT '10-01' AS sort_key, '営業活動によるキャッシュ・フロー' AS section, '税引前当期純利益' AS item, pretax AS amount FROM tot
UNION ALL SELECT '10-02', '営業活動によるキャッシュ・フロー', '減価償却費', dep FROM tot
UNION ALL SELECT '10-03', '営業活動によるキャッシュ・フロー', '売上債権の増減額（△は増加）', ar FROM tot
UNION ALL SELECT '10-04', '営業活動によるキャッシュ・フロー', '棚卸資産の増減額（△は増加）', inv FROM tot
UNION ALL SELECT '10-05', '営業活動によるキャッシュ・フロー', 'その他の流動資産の増減額（△は増加）', oca FROM tot
UNION ALL SELECT '10-06', '営業活動によるキャッシュ・フロー', '仕入債務の増減額（△は減少）', ap FROM tot
UNION ALL SELECT '10-07', '営業活動によるキャッシュ・フロー', 'その他の流動負債の増減額（△は減少）', ocl FROM tot
UNION ALL SELECT '10-08', '営業活動によるキャッシュ・フロー', '小計', op_sub FROM tot
UNION ALL SELECT '10-09', '営業活動によるキャッシュ・フロー', '法人税等の支払額', taxpaid FROM tot
UNION ALL SELECT '10-99', '営業活動によるキャッシュ・フロー', '営業活動によるキャッシュ・フロー', op_cf FROM tot
UNION ALL SELECT '20-01', '投資活動によるキャッシュ・フロー', '固定資産の取得・売却による収支（△は取得）', fa_cf FROM tot
UNION ALL SELECT '20-02', '投資活動によるキャッシュ・フロー', '投資その他の資産の増減額（△は増加）', fao_cf FROM tot
UNION ALL SELECT '20-99', '投資活動によるキャッシュ・フロー', '投資活動によるキャッシュ・フロー', inv_cf FROM tot
UNION ALL SELECT '30-01', '財務活動によるキャッシュ・フロー', '短期借入金の純増減額（△は減少）', sloan FROM tot
UNION ALL SELECT '30-02', '財務活動によるキャッシュ・フロー', '長期借入金の純増減額（△は減少）', lloan FROM tot
UNION ALL SELECT '30-03', '財務活動によるキャッシュ・フロー', '増資等による収入', cap FROM tot
UNION ALL SELECT '30-04', '財務活動によるキャッシュ・フロー', '剰余金の配当等（△は支払）', ret FROM tot
UNION ALL SELECT '30-99', '財務活動によるキャッシュ・フロー', '財務活動によるキャッシュ・フロー', fin_cf FROM tot
UNION ALL SELECT '40-01', '現金及び現金同等物', '現金及び現金同等物の増減額', op_cf + inv_cf + fin_cf FROM tot
UNION ALL SELECT '40-02', '現金及び現金同等物', '現金及び現金同等物の期首残高', cash_open FROM tot
UNION ALL SELECT '40-03', '現金及び現金同等物', '現金及び現金同等物の期末残高', cash_open + op_cf + inv_cf + fin_cf FROM tot
ORDER BY sort_key
