-- 230_bank_transfer.sql — 振込データ作成（全銀フォーマット総合振込）D-6後続
-- partners: 振込先口座情報（全銀データレコードの被仕向側）
-- company_profile: 自社（委託者）情報の恒久的な置き場（v1 の FbExport 画面は画面上の
--   編集可能フィールドで運用するが、将来マスタ画面を作る際の正とするテーブル）
-- 注意: ALTER TABLE ADD COLUMN は再実行するとエラーになるが害はない（既存列は変化しない）

ALTER TABLE partners ADD COLUMN bank_code TEXT;      -- 銀行コード4桁
ALTER TABLE partners ADD COLUMN branch_code TEXT;    -- 支店コード3桁
ALTER TABLE partners ADD COLUMN account_type TEXT;   -- '1'普通 / '2'当座
ALTER TABLE partners ADD COLUMN account_no TEXT;     -- 口座番号7桁
ALTER TABLE partners ADD COLUMN payee_kana TEXT;     -- 受取人名（半角カナ・英大文字・数字。小文字カナ不可）

CREATE TABLE IF NOT EXISTS company_profile (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    consignor_code TEXT,     -- 委託者コード（銀行から付与される10桁）
    consignor_kana TEXT,     -- 委託者名（半角カナ40桁以内）
    bank_code TEXT,          -- 仕向銀行コード4桁
    bank_kana TEXT,          -- 仕向銀行名（半角カナ15桁以内）
    branch_code TEXT,        -- 仕向支店コード3桁
    branch_kana TEXT,        -- 仕向支店名（半角カナ15桁以内）
    account_type TEXT,       -- '1'普通 / '2'当座
    account_no TEXT          -- 口座番号7桁
);

-- ---- seed（デモ用ダミー。実運用では自社の実データに置き換える） ----
INSERT OR IGNORE INTO company_profile
    (id, consignor_code, consignor_kana, bank_code, bank_kana, branch_code, branch_kana, account_type, account_no)
VALUES
    (1, '0000091001', 'ｽﾀｰﾗｲﾄｺﾝｻﾙﾃｲﾝｸﾞ(ｶ', '0001', 'ﾐｽﾞﾎ', '001', 'ﾎﾝﾃﾝ', '1', '1234567');

UPDATE partners SET
    bank_code = '0005', branch_code = '123', account_type = '1',
    account_no = '1111111', payee_kana = 'ｶ)ｱﾙﾀｲﾙｼｮｳｼﾞ'
WHERE code = 'C001' AND bank_code IS NULL;

UPDATE partners SET
    bank_code = '0009', branch_code = '456', account_type = '1',
    account_no = '2222222', payee_kana = 'ｶ)ﾍﾞｶﾞｿﾌﾄ'
WHERE code = 'C002' AND bank_code IS NULL;
