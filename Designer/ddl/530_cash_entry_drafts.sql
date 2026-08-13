-- 530_cash_entry_drafts.sql — 入出金起票の下書き行と、現預金科目フラグ（ADR-0055・2026-08-14）
--
-- 入出金起票を「入れたら即確定」から「下書きに積んで一括起票」へ変える（銀行明細取込 ADR-0012 と同じ
-- ステージング方式）。下書き 1 行 = 仕訳 1 本で、採番は一括起票の実行時に行う。
--
-- 下書きは「打ちかけているメモ」＝個人の作業領域なので **creator を持ち、自分の行だけ**を見る。
-- 銀行明細（会社が取り込んだ事実＝共有物）とはここが違う。

-- **列の型は他テーブルに合わせる**（日付は DATE・日時は DATETIME）。
-- 実測: entry_date を TEXT で作ると CLB が "08/14/2026" 形式で書き込み、SQLite の date() が
-- NULL を返す＝日付比較・ソートが静かに壊れる。DATE affinity なら他テーブルと同じ ISO で入る。
CREATE TABLE IF NOT EXISTS cash_entry_drafts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    entry_date DATE NOT NULL,                                  -- 取引日
    cash_account_id INTEGER REFERENCES accounts(id),           -- 現預金科目（accounts.is_cash_equivalent = 1）
    direction TEXT NOT NULL DEFAULT 'in',                      -- in(入金) / out(出金)
    counter_account_id INTEGER REFERENCES accounts(id),        -- 相手科目
    department_id INTEGER REFERENCES departments(id),          -- 部門（損益科目のときは必須。ADR-0056）
    amount INTEGER,                                            -- 実際に動いた額（税込）
    description TEXT,                                          -- 摘要
    creator INTEGER REFERENCES app_users(id),                  -- CLB 予約名 Creator（保存時に自動セット）
    created_at DATETIME,                                       -- CLB 予約名 CreatedAt
    updater INTEGER REFERENCES app_users(id),
    updated_at DATETIME
);

CREATE INDEX IF NOT EXISTS idx_cash_entry_drafts_creator ON cash_entry_drafts(creator);

-- ---- 現預金科目フラグ ----
-- CashEntry.mod.json の Candidates に "現金,1000" 等が直書きされていたのを廃止し、マスタで決める
-- （CLAUDE.md「税率・税区分・閾値・勘定科目などはマスタ化してハードコードしない」）。
-- 対象は C/F 計算書が「現金及び現金同等物」として扱う 1000〜1099 帯と同じ 4 科目。
-- 以後は口座科目を増やしてもこのフラグを立てるだけで入出金起票に出る。
-- ※ ALTER TABLE は冪等でない（SQLite に ADD COLUMN IF NOT EXISTS が無い）。再実行すると
--    "duplicate column name" で止まる——このファイルは 1 回だけ流す移行スクリプト。
ALTER TABLE accounts ADD COLUMN is_cash_equivalent INTEGER NOT NULL DEFAULT 0;

UPDATE accounts SET is_cash_equivalent = 1 WHERE code BETWEEN '1000' AND '1099';
