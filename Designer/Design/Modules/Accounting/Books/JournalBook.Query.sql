-- 仕訳帳。**明細 1 行が 1 レコード**（docs/research/2026-08-24_仕訳の取引金額での検索.md）。
--
-- 帳簿なので**計上済みだけ**を出す。下書きは帳簿ではない（docs/10 §5）。
-- 検索は優良な電子帳簿の要件（電帳規則 5 ⑤一ハ・電帳通達 8-13〜8-15）を満たす形にしてある。
--   取引年月日と取引金額を**範囲**で指定でき、会計年度（課税期間）と**組み合わせ**られ、
--   「記録事項がない」ことでも探せる（p_blank_field）。
--
-- パラメータは「未指定なら効かない」形で書く。CLB は空の検索欄を NULL または空文字で束縛するので、
-- **両方を見る**（クエリモジュールの定石。_specs/QueryAndSql.md のサンプルも同じ形）。
--
-- 日付は date() を通して比較する。DATE 列の正規形は 'YYYY-MM-DD 00:00:00' で（qa/01 A-04）、
-- 検索欄から来る値が時刻付きとは限らないため、辞書順比較のままでは境界の 1 日が落ちうる。
SELECT
    e.id                        AS entry_id,
    e.entry_no                  AS entry_no,
    -- **会計年度を列に出す。** 伝票番号は年度ごとの連番（I-17）なので、年度で絞らなければ
    -- 同じ「1」が何行も並ぶ。帳簿は既定で絞らない（docs/21 §3）ので、既定の表示は必ずそうなる。
    -- 元帳にも同じ理由で出してある。
    fy.label                    AS fiscal_year_label,
    e.transaction_date          AS transaction_date,
    e.posting_date              AS posting_date,
    -- **区分値は生のまま返す。** 日本語の見出しは CLB のデザイン enum が持っており
    -- （C# の列挙型・DDL の CHECK と 3 者一致を機械で検査している）、
    -- ここで CASE を書くと 4 つ目の写しになる。種別を足したときにここだけ漏れる。
    e.entry_type                AS entry_type,
    orig.entry_no               AS original_entry_no,
    l.line_no                   AS line_no,
    l.debit_credit              AS debit_credit,
    a.name                      AS account_name,
    sa.name                     AS sub_account_name,
    d.name                      AS department_name,
    -- 取引先名は**明細の写しを優先**する。取引先の改名で過去の帳簿の記載が変わらないため
    -- （docs/10 §4-2）。写しが無い行は現在のマスタ名で補う。
    -- 明細の取引先は伝票の既定値を**上書きする**（docs/10 §4-1）ので、明細 → 伝票の順に見る。
    COALESCE(l.partner_name_snapshot, lp.name, ep.name) AS partner_name,
    e.entered_at                AS entered_at,
    l.amount                    AS amount,
    tc.name                     AS tax_category_name,
    l.item_description          AS item_description,
    e.description               AS description
FROM journal_entries e
JOIN journal_lines   l  ON l.journal_entry_id = e.id
JOIN accounts        a  ON a.id  = l.account_id
JOIN tax_categories  tc ON tc.id = l.tax_category_id
-- 会計年度は**列に出すため**と**並び順のため**に引く（識別子ではなく開始日で並べる）。
JOIN fiscal_years    fy ON fy.id = e.fiscal_year_id
LEFT JOIN sub_accounts sa ON sa.id = l.sub_account_id
LEFT JOIN departments  d  ON d.id  = l.department_id
LEFT JOIN partners     lp ON lp.id = l.partner_id
LEFT JOIN partners     ep ON ep.id = e.partner_id
LEFT JOIN journal_entries orig ON orig.id = e.original_entry_id
WHERE e.status = 'posted'
  AND (@p_fiscal_year_id IS NULL OR @p_fiscal_year_id = ''
       OR e.fiscal_year_id = @p_fiscal_year_id)
  AND (@p_transaction_date_from IS NULL OR @p_transaction_date_from = ''
       OR date(e.transaction_date) >= date(@p_transaction_date_from))
  AND (@p_transaction_date_to IS NULL OR @p_transaction_date_to = ''
       OR date(e.transaction_date) <= date(@p_transaction_date_to))
  AND (@p_amount_min IS NULL OR @p_amount_min = '' OR l.amount >= @p_amount_min)
  AND (@p_amount_max IS NULL OR @p_amount_max = '' OR l.amount <= @p_amount_max)
  -- 伝票番号での検索。電帳通達 8-14 (注) の「一連番号等で帳簿間の関連性を確保している場合、
  -- その一連番号等で検索できるとき」に当たる別ルートである（docs/research の電帳法 §3.5）。
  -- **伝票番号は会計年度の中の連番**（005_journals.sql の UNIQUE）なので、
  -- 年度を指定せずに範囲で引くと複数の年度の同じ番号が並ぶ。それは誤りではなく、
  -- 「番号だけで探した」結果である。年度で絞りたいときは会計年度と組み合わせる。
  AND (@p_entry_no_min IS NULL OR @p_entry_no_min = '' OR e.entry_no >= @p_entry_no_min)
  AND (@p_entry_no_max IS NULL OR @p_entry_no_max = '' OR e.entry_no <= @p_entry_no_max)
  AND (@p_account_id IS NULL OR @p_account_id = '' OR l.account_id = @p_account_id)
  -- **表示・検索・空値検索で同じモデルを使う。** ここだけ OR にすると、
  -- 「伝票は甲・明細は乙」の行が「甲」で引けるのに帳簿には「乙」と出る——
  -- 法定記載事項を条件にした検索が、記載と違う行を返すことになる。
  AND (@p_partner_id IS NULL OR @p_partner_id = ''
       OR COALESCE(l.partner_id, e.partner_id) = @p_partner_id)
  -- 摘要・内容の部分一致。**利用者が打った文字をワイルドカードにしない。**
  -- 素通しにすると「%」を含む摘要を探せないうえ、「%」だけを打つと全件に当たる。
  -- 逃がす順序は「まず \ を、次に % と _ を」。逆にすると付けたばかりの \ をもう一度逃がす。
  AND (@p_keyword IS NULL OR @p_keyword = ''
       OR e.description LIKE
          '%' || replace(replace(replace(@p_keyword, '\', '\\'), '%', '\%'), '_', '\_') || '%' ESCAPE '\'
       OR l.item_description LIKE
          '%' || replace(replace(replace(@p_keyword, '\', '\\'), '%', '\%'), '_', '\_') || '%' ESCAPE '\')
  -- 電帳通達 8-13「検索項目について記録事項がない電磁的記録を検索できる機能」。
  -- **空文字も「無い」として扱う。** 本来は NULL の 1 通りに寄せる方針だが（docs/10 §4-4）、
  -- 画面から空文字が入る経路が塞ぎ切れていないうちは、両方を拾わないと取りこぼす。
  AND (@p_blank_field IS NULL OR @p_blank_field = ''
       OR (@p_blank_field = 'partner'
           AND COALESCE(l.partner_id, e.partner_id) IS NULL
           AND (l.partner_name_snapshot IS NULL OR l.partner_name_snapshot = ''))
       OR (@p_blank_field = 'department'  AND l.department_id IS NULL)
       OR (@p_blank_field = 'sub_account' AND l.sub_account_id IS NULL)
       OR (@p_blank_field = 'description'
           AND (e.description IS NULL OR e.description = ''))
       OR (@p_blank_field = 'item_description'
           AND (l.item_description IS NULL OR l.item_description = '')))
-- **先頭は取引日である。** 仕訳帳は「取引の発生順に」記載する（法税規則 55 ①。docs/21 §3）。
--
-- **年度を並び順に含める。** 伝票番号は年度内の連番なので（005_journals.sql の UNIQUE）、
-- 年度を無視すると、3 月の仕訳を 4 月に取り消したときに
-- 「取引日が同じで番号が小さい取消」が原仕訳より前に並ぶ。
--
-- **年度は開始日で並べる。識別子で並べない。** fiscal_years.id は AUTOINCREMENT の代理キーで、
-- 年代とは無関係な挿入順である——第 17 期を後から入れると id は第 18 期より大きくなる（qa/03 L-19）。
-- date() で包むのは、格納形が 'YYYY-MM-DD' と 'YYYY-MM-DD 00:00:00' の両方をとりうるため（qa/01 A-04）。
ORDER BY date(e.transaction_date), date(fy.start_date), e.entry_no, l.line_no
