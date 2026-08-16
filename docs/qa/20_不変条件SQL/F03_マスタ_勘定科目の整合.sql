-- 何を保証するか: 勘定科目マスタが会計上つじつまの合う定義になっていること。
--   (a) dc_normal（正残の側）が account_type と一致する
--       資産・費用 = D／負債・純資産・収益 = C（貸倒引当金等の評価勘定は例外なので下の備考を参照）
--   (b) 表示区分（account_categories.statement）が BS/PL と account_type の対応と矛盾しない
--   (c) account_type が定義済みの 5 種類のいずれか
--   (d) コードが 4 桁の数字
-- 違反時の意味: 試算表・BS/PL の符号が反転する。残高の表示は dc_normal で正負を反転させるため、
--               ここが狂うと「利益が損失に見える」レベルの表示事故になる。
-- 出典: docs/04_会計ドメイン設計.md §2（accounts / account_categories）・§2.1（コード体系）
-- 除外（意図的に正残が逆側の科目）:
--   1950 貸倒引当金   … 資産の評価勘定（資産だが貸方残）
--   5900 仕掛品振替高 … 売上原価のマイナス科目（費用だが貸方残）
--   これ以外に評価勘定を増やしたら、この除外リストに追記すること。
--   ※逆に言えば、リストに無い科目が引っかかったら本物の設定ミス。
SELECT '正残の側が科目区分と不一致' AS 違反, a.code AS コード, a.name AS 科目名,
       a.account_type AS 科目区分, a.dc_normal AS 正残側, NULL AS 表示区分
FROM accounts a
WHERE a.code NOT IN ('1950', '5900')
  AND ((a.account_type IN ('asset', 'expense')               AND a.dc_normal <> 'D')
    OR (a.account_type IN ('liability', 'equity', 'revenue') AND a.dc_normal <> 'C'))

UNION ALL
SELECT '表示区分(BS/PL)が科目区分と不一致', a.code, a.name, a.account_type, a.dc_normal, c.statement
FROM accounts a
JOIN account_categories c ON c.id = a.category_id
WHERE (a.account_type IN ('asset', 'liability', 'equity') AND c.statement NOT IN ('BS', 'SS'))
   OR (a.account_type IN ('revenue', 'expense')           AND c.statement <> 'PL')

UNION ALL
SELECT '科目区分が未定義の値', a.code, a.name, a.account_type, a.dc_normal, NULL
FROM accounts a
WHERE a.account_type NOT IN ('asset', 'liability', 'equity', 'revenue', 'expense')

UNION ALL
SELECT 'コードが4桁数字でない', a.code, a.name, a.account_type, a.dc_normal, NULL
FROM accounts a
WHERE LENGTH(a.code) <> 4 OR a.code GLOB '*[^0-9]*'
