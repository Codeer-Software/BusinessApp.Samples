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
--   地方税法 72 の 83（令和8年7月31日施行版）  標準・軽減の地方    七十八分の二十二
--
-- **法源には、逐語を読んだ版そのものを書く。** 「2019-10-01 から効いている」ことと
-- 「どの版の条文を開いたか」は別で、**開いていない版を名乗ると、出典をたどっても字が確かめられない**
-- （2026-09-23 の自己レビュー。一度「令和元年10月1日施行版」と書いていた）。
-- **出典 URL も版つきにする**——版なしの索引 URL は現行版を表示するので、
-- **過去の版を開きたいのに、いまの版の字が出る**（2026-09-23 に旧税率の行で踏んだ。旧税率の行は 2026-09-24 に外した）。
--
-- **検算**（TaxRateTests が同じことを見る）——国税 ×（分母＋分子）÷ 分母 が合計税率になる。
--   780 × 100 ÷ 78 = 1000（10%）／ 624 × 100 ÷ 78 = 800（8%）
--
-- **2 行とも終期が無い。** 消税法 29 も地方税法 72 の 83 も、期限を書いていない。
-- 014 の控除割合とはここが違う（あちらは条文が終わりを書いている）。
--
-- **旧税率 8% の行は配らない**——スコープの外である（docs/11 §1-1-1）。
--
-- **2019-09-30 以前の行は配らない。** いつから 6.3% かを確かめていない
-- （e-Gov の law_revisions は 2015-01-01 より前の版を返さない。税率リサーチ §3）。
-- **この会計コアが扱う帳簿はそれよりずっと後から始まる**ので、いま要らない。
--
-- **2019-10-01 から今日までのあいだに条文が動いていないことは確かめていない**（税率リサーチ §3）。
-- **開いたのは新旧 2 つの版だけで、そのあいだの版を law_revisions で数えていない。**
-- **動いていれば、2 行とも期間を割ることになる。**
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
