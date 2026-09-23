-- 0040 経過措置の控除割合の 4 行を配る（Designer/seed/005_transition_rates.sql）
--
-- **本プロジェクトで初めての「データ行を配る」マイグレーションである**（migrations/README）。
-- **同値の網は同じ回に入れた**——VendorRows（MigrationEquivalenceTests と migrate.ps1 -Verify）。
-- 網が無いまま行を配ると、**稼働 DB の控除割合が正典と違っていても誰も気づかない**。
--
-- **値の出どころと、行を分ける根拠は seed/005 が持つ。** ここは配達物なので写さない。
-- **NOT EXISTS で包んである**ので、二度流しても行は増えない。

INSERT INTO transition_purchase_rates
    (valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)
SELECT '2023-10-01', '2026-09-30', 80, 'transition_purchase_rate:2023-10-01',
       '所税法等一部改正法（平成28年法律第15号）附則 52 ①',
       'https://laws.e-gov.go.jp/law/363AC0000000108', '2026-09-10'
 WHERE NOT EXISTS (SELECT 1 FROM transition_purchase_rates
                    WHERE version = 'transition_purchase_rate:2023-10-01');

INSERT INTO transition_purchase_rates
    (valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)
SELECT '2026-10-01', '2028-09-30', 70, 'transition_purchase_rate:2026-10-01',
       '所税法等一部改正法（平成28年法律第15号）附則 53 ① 一',
       'https://laws.e-gov.go.jp/law/363AC0000000108', '2026-09-10'
 WHERE NOT EXISTS (SELECT 1 FROM transition_purchase_rates
                    WHERE version = 'transition_purchase_rate:2026-10-01');

INSERT INTO transition_purchase_rates
    (valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)
SELECT '2028-10-01', '2030-09-30', 50, 'transition_purchase_rate:2028-10-01',
       '所税法等一部改正法（平成28年法律第15号）附則 53 ① 二',
       'https://laws.e-gov.go.jp/law/363AC0000000108', '2026-09-10'
 WHERE NOT EXISTS (SELECT 1 FROM transition_purchase_rates
                    WHERE version = 'transition_purchase_rate:2028-10-01');

INSERT INTO transition_purchase_rates
    (valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)
SELECT '2030-10-01', '2031-09-30', 30, 'transition_purchase_rate:2030-10-01',
       '所税法等一部改正法（平成28年法律第15号）附則 53 ① 三',
       'https://laws.e-gov.go.jp/law/363AC0000000108', '2026-09-10'
 WHERE NOT EXISTS (SELECT 1 FROM transition_purchase_rates
                    WHERE version = 'transition_purchase_rate:2030-10-01');
