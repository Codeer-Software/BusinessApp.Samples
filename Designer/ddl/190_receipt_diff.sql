-- 190_receipt_diff.sql — 入金消込の差額自動処理の閾値（磨きバックログ「消込差額処理」）
-- 入金額が請求残額に対して不足するとき、差額がこの金額以下なら
-- 振込手数料等とみなして支払手数料(6210)で自動仕訳し、請求を消込（paid）にする。
-- 閾値はマスタ参照（ハードコード禁止の原則）。code に UNIQUE が無いため NOT EXISTS で冪等化。

INSERT INTO system_thresholds (code, name, amount, valid_from, valid_to)
SELECT 'RECEIPT_DIFF_MAX', '入金消込の差額自動処理 上限', 1000, NULL, NULL
WHERE NOT EXISTS (SELECT 1 FROM system_thresholds WHERE code = 'RECEIPT_DIFF_MAX');
