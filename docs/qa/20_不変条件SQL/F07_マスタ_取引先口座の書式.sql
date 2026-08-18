-- 何を保証するか: 取引先口座（`partners` の口座 5 項目）が「全部有効」か「全部空」のどちらかであること。
--   (a) 部分入力が無い（5 項目のうち一部だけ埋まっている行が無い）
--   (b) 銀行コード 4 桁 / 支店コード 3 桁 / 口座番号 7 桁（すべて数字）
--   (c) 受取人名が全銀フォーマットの文字種（半角カナ・英大文字・数字・記号）だけでできている
-- 違反時の意味: 振込データ（FB）を作ったときに、**その仕入先だけが黙って除外される**。
--               除外理由は画面に出るが、何十社ぶんの一覧の末尾に並ぶだけなので読み落とす。
--               「振り込んだつもりで振り込んでいない」は支払遅延として現れる。
-- 出典: `Designer/Design/Modules/MasterBusiness/PartnerBank.mod.cs` の `KanaError()`（文字種の正典）／
--       BUG-0045（検証が画面に繋がっていなかった）／BUG-0418（全角のまま保存された 2 件）
-- 備考: 口座 5 項目が全て空の取引先（口座未登録）は正常なので対象外。
--       (c) は 1 文字ずつ `unicode()` でコードポイントを見る（hex の部分一致は
--       バイト境界をまたいで誤検知するので使わない）。許容範囲:
--         U+FF71〜U+FF9F ｱ〜ﾝ・濁点・半濁点 ／ U+FF66 ｦ ／ U+FF70 ｰ ／ U+FF62 ｢ ／ U+FF63 ｣
--         U+0041〜U+005A 英大文字 ／ U+0030〜U+0039 数字
--         U+0020 空白 ／ U+0028 ( ／ U+0029 ) ／ U+002D - ／ U+002E . ／ U+002F /
--       **小書きカナ（ｧｨｩｪｫｬｭｮｯ）は全銀で使えない**ので許容しない。`ｼｮ` は `ｼﾖ` と書く。
--       スクリプト側の許容集合を変えるときは、ここも直す。

WITH acc AS (
  SELECT id, code, name,
         COALESCE(bank_code,'')    AS bank_code,
         COALESCE(branch_code,'')  AS branch_code,
         COALESCE(account_type,'') AS account_type,
         COALESCE(account_no,'')   AS account_no,
         COALESCE(payee_kana,'')   AS payee_kana
  FROM partners
),
filled AS (
  SELECT *,
         (CASE WHEN bank_code    <> '' THEN 1 ELSE 0 END)
       + (CASE WHEN branch_code  <> '' THEN 1 ELSE 0 END)
       + (CASE WHEN account_type <> '' THEN 1 ELSE 0 END)
       + (CASE WHEN account_no   <> '' THEN 1 ELSE 0 END)
       + (CASE WHEN payee_kana   <> '' THEN 1 ELSE 0 END) AS n
  FROM acc
),
kana(code, name, s, i, c) AS (
  SELECT code, name, payee_kana, 1, substr(payee_kana, 1, 1)
  FROM filled WHERE n = 5 AND payee_kana <> ''
  UNION ALL
  SELECT code, name, s, i + 1, substr(s, i + 1, 1)
  FROM kana WHERE i < length(s)
),
bad_kana AS (
  SELECT DISTINCT code, name, s
  FROM kana
  WHERE c <> ''
    AND NOT (
      (unicode(c) BETWEEN 65393 AND 65439)   -- U+FF71〜U+FF9F ｱ〜ﾝ＋濁点・半濁点（**小書きカナは含まない**）
   OR unicode(c) IN (65382, 65392)           -- ｦ / ｰ
   OR unicode(c) IN (65378, 65379)           -- ｢ ｣
   OR (unicode(c) BETWEEN 65 AND 90)         -- A-Z
   OR (unicode(c) BETWEEN 48 AND 57)         -- 0-9
   OR unicode(c) IN (32, 40, 41, 45, 46, 47) -- 空白 ( ) - . /
    )
)
SELECT '口座の部分入力' AS 違反, code AS 取引先, name AS 名称, payee_kana AS 値 FROM filled WHERE n > 0 AND n < 5

UNION ALL
SELECT '銀行コードが数字4桁でない', code, name, bank_code FROM filled
 WHERE n = 5 AND (length(bank_code) <> 4 OR bank_code GLOB '*[^0-9]*')

UNION ALL
SELECT '支店コードが数字3桁でない', code, name, branch_code FROM filled
 WHERE n = 5 AND (length(branch_code) <> 3 OR branch_code GLOB '*[^0-9]*')

UNION ALL
SELECT '口座番号が数字7桁でない', code, name, account_no FROM filled
 WHERE n = 5 AND (length(account_no) <> 7 OR account_no GLOB '*[^0-9]*')

UNION ALL
SELECT '受取人名に全銀で使えない文字がある', code, name, s FROM bad_kana
;
