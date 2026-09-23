-- 006 税率（制度ルール。ddl/015_tax_rates.sql）
--
-- **005 と同じ性質である**——**ベンダーが配り、利用者は直さない**（docs/12_マスタ台帳・ADR-0020）。
-- **行の中身が正典どおりかを機械が見ている**（migrate.ps1 -Verify と MigrationEquivalenceTests）。
--
-- **値は条文から採った。** 記憶で書かない（CLAUDE.md §2）。
-- 逐語と、切り替わった日は docs/research/2026-09-23_消費税の税率と地方消費税の税率.md が持つ。
--
--   消税法 29 一（令和8年4月1日施行版）        標準税率の国税      百分の七・八   → 万分の 780
--   消税法 29 二（同）                          軽減税率の国税      百分の六・二四 → 万分の 624
--   消税法 29（令和元年7月1日施行版）          旧税率 8% の国税    百分の六・三   → 万分の 630
--   地方税法 72 の 83（令和8年7月31日施行版）  標準・軽減の地方    七十八分の二十二
--   地方税法 72 の 83（令和元年9月14日施行版） 旧税率 8% の地方    六十三分の十七
--
-- **法源には、逐語を読んだ版そのものを書く。** 「2019-10-01 から効いている」ことと
-- 「どの版の条文を開いたか」は別で、**開いていない版を名乗ると、出典をたどっても字が確かめられない**
-- （2026-09-23 の自己レビュー。一度「令和元年10月1日施行版」と書いていた）。
-- **出典 URL も版つきにする**——版なしの索引 URL は現行版を表示するので、
-- **旧税率の行の出典を開くと「百分の七・八」が出る**。
--
-- **検算**（TaxRateTests が同じことを見る）——国税 ×（分母＋分子）÷ 分母 が合計税率になる。
--   780 × 100 ÷ 78 = 1000（10%）／ 624 × 100 ÷ 78 = 800（8%）／ 630 × 80 ÷ 63 = 800（8%）
--
-- **3 行とも終期が無い。** 消税法 29 も地方税法 72 の 83 も、期限を書いていない。
-- 014 の控除割合とはここが違う（あちらは条文が終わりを書いている）。
--
-- **`legacy_8` を 2019-10-01 からの行にしたのは Claude の当てはめである**（開発者は未承認。docs/05 の Q-54）。
-- **6.3% と 63 分の 17 は、条文の上では 2019-09-30 までの税率**だが、
-- **経過措置で今日の取引にも使われる、と Claude は理解している**（条文は未確認。税率リサーチ §3）——
-- その理解に立つと、「2019-09-30 まで」の行にしたのでは今日の取引で税率が引けない。
-- **この表は「いつ legacy_8 を選んでよいか」を持たない**（経過措置の条文を開いていない。税率リサーチ §3）。
--
-- **2019-09-30 以前の行は配らない。** いつから 6.3% かを確かめていない
-- （e-Gov の law_revisions は 2015-01-01 より前の版を返さない。税率リサーチ §3）。
-- **この会計コアが扱う帳簿はそれよりずっと後から始まる**ので、いま要らない。
--
-- **2019-10-01 から今日までのあいだに条文が動いていないことは確かめていない**（税率リサーチ §3）。
-- **開いたのは新旧 2 つの版だけで、そのあいだの版を law_revisions で数えていない。**
-- **動いていれば、3 行とも期間を割ることになる。**
--
-- **どの行も出典の URL を 2 本持つ。** 国税と地方で法令が違うからで、空白で区切って並べてある。

INSERT INTO tax_rates
    (rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator, local_denominator,
     version, legal_basis, source_url, confirmed_on)
SELECT 'standard', '2019-10-01', NULL, 780, 22, 78,
       'tax_rate:standard:2019-10-01',
       '消税法 29 一（令和8年4月1日施行版）・地方税法 72 の 83（令和8年7月31日施行版）',
       'https://laws.e-gov.go.jp/api/2/law_data/363AC0000000108_20260401_508AC0000000012'
       || ' https://laws.e-gov.go.jp/api/2/law_data/325AC0000000226_20260731_508AC0000000002',
       '2026-09-23'
 WHERE NOT EXISTS (SELECT 1 FROM tax_rates WHERE version = 'tax_rate:standard:2019-10-01');

INSERT INTO tax_rates
    (rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator, local_denominator,
     version, legal_basis, source_url, confirmed_on)
SELECT 'reduced', '2019-10-01', NULL, 624, 22, 78,
       'tax_rate:reduced:2019-10-01',
       '消税法 29 二（令和8年4月1日施行版）・地方税法 72 の 83（令和8年7月31日施行版）',
       'https://laws.e-gov.go.jp/api/2/law_data/363AC0000000108_20260401_508AC0000000012'
       || ' https://laws.e-gov.go.jp/api/2/law_data/325AC0000000226_20260731_508AC0000000002',
       '2026-09-23'
 WHERE NOT EXISTS (SELECT 1 FROM tax_rates WHERE version = 'tax_rate:reduced:2019-10-01');

INSERT INTO tax_rates
    (rate_kind, valid_from, valid_to, national_rate_per_10000, local_numerator, local_denominator,
     version, legal_basis, source_url, confirmed_on)
SELECT 'legacy_8', '2019-10-01', NULL, 630, 17, 63,
       'tax_rate:legacy_8:2019-10-01',
       '消税法 29（令和元年7月1日施行版）・地方税法 72 の 83（令和元年9月14日施行版）',
       'https://laws.e-gov.go.jp/api/2/law_data/363AC0000000108_20190701_431AC0000000006'
       || ' https://laws.e-gov.go.jp/api/2/law_data/325AC0000000226_20190914_501AC0000000037',
       '2026-09-23'
 WHERE NOT EXISTS (SELECT 1 FROM tax_rates WHERE version = 'tax_rate:legacy_8:2019-10-01');
