-- 240_subaccount_seed.sql — 補助科目のデモ用 seed（補助元帳の絞り込みデモを可能にする）
-- 売掛金(1100): 取引先別 ／ 普通預金(1020): 口座別 ／ 買掛金(2000): 仕入先別
-- 既存仕訳への遡及付与はしない（今後の起票で補助科目を選べるようにするのが目的）

INSERT OR IGNORE INTO sub_accounts (account_id, code, name) VALUES
    ((SELECT id FROM accounts WHERE code = '1100'), 'C001', '株式会社アルタイル商事'),
    ((SELECT id FROM accounts WHERE code = '1100'), 'C003', '株式会社テックイノベーション'),
    ((SELECT id FROM accounts WHERE code = '1020'), 'MB01', 'メインバンク 普通預金'),
    ((SELECT id FROM accounts WHERE code = '2000'), 'C002', '株式会社ベガソフト');
