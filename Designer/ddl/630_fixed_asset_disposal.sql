-- 630_fixed_asset_disposal.sql — 固定資産の除却・売却を帳簿に載せる（BUG-0095・BusinessAppSQLite）
--
-- 【適用済み】2026-08-18 に BusinessAppSQLite へ適用した（BUG-0095 の実装と同時）。再実行可。
--
-- 【問題】固定資産台帳は status に retired/sold を持てるのに、状態を変えても仕訳は 1 行も立たない。
--   資産は BS に簿価のまま残り、除却損（8500）・売却損益は PL に出ない。売却価額を入れる欄すら無い。
--   さらに償却生成が状態を見ないので、除却済みの資産に翌年度の償却仕訳を作れてしまう。
--
-- 【この DDL で足すもの】
--   (1) fixed_assets.disposal_amount … 売却価額。処分区分は既存の status（in_use/retired/sold）が
--       そのまま担うので新設しない（同じ意味の列を 2 本持つと必ず食い違う）。
--       処分日は既存の retired_date を使う。
--   (2) accounts.account_role … 「この科目がアプリのどの役割を担うか」を科目マスタ側に持たせる。
--       科目コードの直値（'6300' 等）をスクリプトに書かないための列。
--       BUG-0054（1900/2200 直値）・BUG-0118（6300 直値）と同じ根に対する受け皿でもある。
--       先例: is_cash_equivalent（ADR-0055）・is_fixed_asset_account（ADR-0063）。
--       ただしそれらが「多数の科目に立つフラグ」なのに対し、こちらは **1 役割 = 1 科目** なので
--       真偽値の列を役割の数だけ増やすのではなく、値で役割を表す 1 列 + 一意制約にする。
--   (3) 8510 固定資産売却損 … **既存マスタに無かった**（8000 売却益・8500 除却損はあるが売却損が欠落）。
--       売却価額 < 帳簿価額のとき振替先が無く、この科目が無いと売却仕訳が作れない。
--       帯域は docs/04 §3 の 8000-8999（特別損益）に従い、除却損 8500 の隣に 8510 を置く。

-- ---- (1) 固定資産台帳: 売却価額 ----
ALTER TABLE fixed_assets ADD COLUMN disposal_amount INTEGER;   -- 売却価額（税抜・売却のときだけ入る）

-- ---- (2) 勘定科目: 機能役割 ----
ALTER TABLE accounts ADD COLUMN account_role TEXT;             -- 1 役割 1 科目。未設定は NULL

-- 同じ役割の科目が 2 つあると、どちらが使われるか分からない静かな失敗になる。
-- SQLite の UNIQUE は NULL を重複扱いしないが、意図を明示するため部分インデックスにする。
CREATE UNIQUE INDEX IF NOT EXISTS ux_accounts_account_role
    ON accounts(account_role) WHERE account_role IS NOT NULL;

-- ---- (3) 固定資産売却損（新規科目） ----
-- 税区分は除却損（8500）に揃えて「対象外」。売却の消費税は現状アプリが自動計算しない
-- （売却仕訳は内部振替と同じく全明細を対象外で起票する）。
INSERT INTO accounts
    (code, name, kana, account_type, category_id, dc_normal,
     default_tax_category_id, display_order, is_active, is_cash_equivalent,
     is_fixed_asset_account, account_role)
SELECT '8510', '固定資産売却損', NULL, 'expense',
       (SELECT id FROM account_categories WHERE code = 'EL'),      -- 特別損失
       'D',
       (SELECT id FROM tax_categories WHERE code = 'OUT_OF_SCOPE'),
       8510, 1, 0, 0, 'sale_loss'
WHERE NOT EXISTS (SELECT 1 FROM accounts WHERE code = '8510');

-- ---- 役割の割り当て ----
--   depreciation_expense … 償却仕訳の借方（従来 '6300' 直値だった箇所）
--   disposal_loss        … 除却時に帳簿価額を振り替える先
--   sale_gain / sale_loss… 売却価額と帳簿価額の差額の振替先
--   disposal_receivable  … 売却代金の未収（入金は入金処理で消し込む）
UPDATE accounts SET account_role = 'depreciation_expense' WHERE code = '6300';
UPDATE accounts SET account_role = 'disposal_loss'        WHERE code = '8500';
UPDATE accounts SET account_role = 'sale_gain'            WHERE code = '8000';
UPDATE accounts SET account_role = 'sale_loss'            WHERE code = '8510';
UPDATE accounts SET account_role = 'disposal_receivable'  WHERE code = '1110';

-- 確認
SELECT code, name, account_type, account_role FROM accounts
 WHERE account_role IS NOT NULL ORDER BY code;
