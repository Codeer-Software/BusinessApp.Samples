-- 005 経過措置の控除割合（制度ルール。ddl/014_transition_rates.sql）
--
-- **ここだけ性質が違う。** 001〜004 は「利用者が後から直すマスタの初期値」だが、
-- **この 4 行はベンダーが配り、利用者は直さない**（docs/12_マスタ台帳・ADR-0020）。
-- **行の中身が正典どおりかを機械が見ている**——migrate.ps1 -Verify と MigrationEquivalenceTests。
--
-- **値は条文から採った。** 記憶で書かない（CLAUDE.md §2）。
-- 出典は docs/research/2026-09-11_インボイス経過措置の附則条文.md §2-3・§2-4
-- （**令和8年10月1日施行版**。所税法等一部改正法（令和8年法律第12号）附則 1 ① 三 が施行期日）。
--
--   附則 52 ①  五年施行日（2023-10-01）から適用期限（2026-09-30）まで  百分の八十
--   附則 53 ① 一  適用期限の翌日（2026-10-01）から令和十年九月三十日（2028-09-30）まで  百分の七十
--   附則 53 ① 二  令和十年十月一日（2028-10-01）から令和十二年九月三十日（2030-09-30）まで  百分の五十
--   附則 53 ① 三  令和十二年十月一日（2030-10-01）から令和十三年九月三十日（2031-09-30）まで  百分の三十
--
-- **2031-10-01 以後の行は作らない。** 経過措置が終わったことは「行が無いこと」で表す（014 の注記）。
--
-- **70/50/30 の 3 行は未施行の版である**（施行は 2026-10-01。今日はまだ現行版が効いていて、
-- そちらの附則 53 ① は号を持たず「適用期限の翌日から三年、百分の五十」の 1 文である）。
-- **だから法源に施行版を書く**——**書かないと、出典 URL を開いても指した号が見つからない**。
-- **条文リサーチは未確認を 2 つ抱えたままである**（§6 の未確認事項 3・7——
-- 本則 14 の「（略）」に 53 の各号が含まれるか、施行版がどの課税仕入れから当たるか）。
-- **値そのもの（70/50/30）は §2-4 の逐語と一致している**ので、未確認なのは当てはめの範囲である（docs/32 §3）。
--
-- **リサーチの §1-1 の表は 50% を 2 行に割っているが、それは改正前との対比のためである**——
-- **改正後の条文（附則 53 ① 二）は 2028-10-01〜2030-09-30 を 1 つの号にまとめて書いている**ので、ここでは 1 行にする。

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
       '所税法等一部改正法（平成28年法律第15号）附則 53 ① 一（令和8年10月1日施行版）',
       'https://laws.e-gov.go.jp/law/363AC0000000108', '2026-09-10'
 WHERE NOT EXISTS (SELECT 1 FROM transition_purchase_rates
                    WHERE version = 'transition_purchase_rate:2026-10-01');

INSERT INTO transition_purchase_rates
    (valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)
SELECT '2028-10-01', '2030-09-30', 50, 'transition_purchase_rate:2028-10-01',
       '所税法等一部改正法（平成28年法律第15号）附則 53 ① 二（令和8年10月1日施行版）',
       'https://laws.e-gov.go.jp/law/363AC0000000108', '2026-09-10'
 WHERE NOT EXISTS (SELECT 1 FROM transition_purchase_rates
                    WHERE version = 'transition_purchase_rate:2028-10-01');

INSERT INTO transition_purchase_rates
    (valid_from, valid_to, rate_percent, version, legal_basis, source_url, confirmed_on)
SELECT '2030-10-01', '2031-09-30', 30, 'transition_purchase_rate:2030-10-01',
       '所税法等一部改正法（平成28年法律第15号）附則 53 ① 三（令和8年10月1日施行版）',
       'https://laws.e-gov.go.jp/law/363AC0000000108', '2026-09-10'
 WHERE NOT EXISTS (SELECT 1 FROM transition_purchase_rates
                    WHERE version = 'transition_purchase_rate:2030-10-01');
