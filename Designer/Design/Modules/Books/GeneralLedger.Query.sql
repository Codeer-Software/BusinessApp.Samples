-- 総勘定元帳。**明細 1 行が 1 レコード**（仕訳帳と同じ形。ADR-0022）。
--
-- 帳簿なので**計上済みだけ**を出す。下書きは帳簿ではない（docs/04 §5）。
--
-- **勘定科目ごとに並べ、勘定科目ごとに累計する。** 勘定科目を指定しなければ全科目が科目コード順に
-- 並ぶ（それが「総勘定元帳」そのものである）。1 科目に絞れば 1 科目の元帳、
-- 補助科目・取引先まで絞れば補助元帳になる（法令が名前を与えている帳簿は仕訳帳と総勘定元帳だけ。
-- ADR-0017）。
--
-- 記録項目（通達 8-14）:
--   総勘定元帳 = 記載年月日・取引金額／補助元帳 = 記録又は取引の年月日・取引金額・取引先名称。
--   **記載年月日は、個々の取引を転記する場合は転記した取引の取引年月日**（一問一答 問 33）なので、
--   ここでは取引日で引く。取引先でも引けるようにしてあるので、補助元帳の記録項目も満たす。
--   伝票番号でも引ける——通達 8-14 (注) の「一連番号等で検索できるとき」の経路である。
--
-- パラメータは「未指定なら効かない」形で書く。CLB は空の検索欄を NULL または空文字で束縛するので、
-- **両方を見る**（仕訳帳と同じ定石）。
SELECT
    e.id                        AS entry_id,
    e.entry_no                  AS entry_no,
    a.code                      AS account_code,
    a.name                      AS account_name,
    sa.name                     AS sub_account_name,
    e.transaction_date          AS transaction_date,
    -- **区分値は生のまま返す。** 日本語の見出しは CLB のデザイン enum が持っている
    -- （C# の列挙型・DDL の CHECK と 3 者一致を機械で検査している）。
    e.entry_type                AS entry_type,
    -- **相手勘定科目**（法人税法施行規則 55 ② の法定記載事項）。
    -- 反対側の行の科目が 1 種類ならその名前、2 種類以上なら「諸口」。
    -- **「諸口」は区分値の写しではない**——複数であることを表す簿記の語であり、
    -- 対応する列挙型も CHECK も存在しない（だからここに書いてよい）。
    (SELECT CASE
                WHEN COUNT(DISTINCT o.account_id) = 1 THEN MIN(oa.name)
                WHEN COUNT(DISTINCT o.account_id) > 1 THEN '諸口'
            END
       FROM journal_lines o
       JOIN accounts     oa ON oa.id = o.account_id
      WHERE o.journal_entry_id = l.journal_entry_id
        AND o.debit_credit <> l.debit_credit)  AS counter_account_name,
    d.name                      AS department_name,
    -- 取引先名は**明細の写しを優先**する（docs/04 §4-2）。仕訳帳と同じ規則で引く。
    COALESCE(l.partner_name_snapshot, lp.name, ep.name) AS partner_name,
    -- 借方と貸方を別の列に出す。**片方は NULL** になり、画面では空欄になる。
    CASE WHEN l.debit_credit = 'debit'  THEN l.amount END AS debit_amount,
    CASE WHEN l.debit_credit = 'credit' THEN l.amount END AS credit_amount,
    -- **期間内累計。** 期首残高（フェーズ 4）が無いので、残高ではなく
    -- 「ここに出ている範囲の先頭からの累計」である。画面の列名もそう名づけてある。
    --
    -- 符号は**その科目の通常残高の側**に合わせる（docs/04 §6）。科目区分だけでは決まらない——
    -- 評価勘定（減価償却累計額・売上値引戻り高など）は通常残高が科目区分と逆になる。
    -- 借方が通常なら「借方 − 貸方」、貸方が通常なら「貸方 − 借方」。
    -- こうしないと売上の累計が負の数で出る。
    SUM(
        CASE WHEN l.debit_credit = 'debit' THEN l.amount ELSE -l.amount END
        * CASE WHEN (a.category IN ('asset', 'expense')) <> (a.is_contra = 1) THEN 1 ELSE -1 END
    ) OVER (
        PARTITION BY l.account_id
        ORDER BY e.transaction_date, e.fiscal_year_id, e.entry_no, l.line_no
    )                           AS running_total,
    l.item_description          AS item_description,
    e.description               AS description
FROM journal_entries e
JOIN journal_lines   l  ON l.journal_entry_id = e.id
JOIN accounts        a  ON a.id  = l.account_id
LEFT JOIN sub_accounts sa ON sa.id = l.sub_account_id
LEFT JOIN departments  d  ON d.id  = l.department_id
LEFT JOIN partners     lp ON lp.id = l.partner_id
LEFT JOIN partners     ep ON ep.id = e.partner_id
WHERE e.status = 'posted'
  AND (@p_fiscal_year_id IS NULL OR @p_fiscal_year_id = ''
       OR e.fiscal_year_id = @p_fiscal_year_id)
  AND (@p_account_id IS NULL OR @p_account_id = '' OR l.account_id = @p_account_id)
  AND (@p_sub_account_id IS NULL OR @p_sub_account_id = '' OR l.sub_account_id = @p_sub_account_id)
  AND (@p_department_id IS NULL OR @p_department_id = '' OR l.department_id = @p_department_id)
  -- 表示・検索・空値検索で同じモデルを使う（仕訳帳と同じ理由）。
  AND (@p_partner_id IS NULL OR @p_partner_id = ''
       OR COALESCE(l.partner_id, e.partner_id) = @p_partner_id)
  AND (@p_transaction_date_from IS NULL OR @p_transaction_date_from = ''
       OR date(e.transaction_date) >= date(@p_transaction_date_from))
  AND (@p_transaction_date_to IS NULL OR @p_transaction_date_to = ''
       OR date(e.transaction_date) <= date(@p_transaction_date_to))
  AND (@p_amount_min IS NULL OR @p_amount_min = '' OR l.amount >= @p_amount_min)
  AND (@p_amount_max IS NULL OR @p_amount_max = '' OR l.amount <= @p_amount_max)
  AND (@p_entry_no_min IS NULL OR @p_entry_no_min = '' OR e.entry_no >= @p_entry_no_min)
  AND (@p_entry_no_max IS NULL OR @p_entry_no_max = '' OR e.entry_no <= @p_entry_no_max)
  -- 摘要・内容の部分一致。**利用者が打った文字をワイルドカードにしない**（仕訳帳と同じ）。
  AND (@p_keyword IS NULL OR @p_keyword = ''
       OR e.description LIKE
          '%' || replace(replace(replace(@p_keyword, '\', '\\'), '%', '\%'), '_', '\_') || '%' ESCAPE '\'
       OR l.item_description LIKE
          '%' || replace(replace(replace(@p_keyword, '\', '\\'), '%', '\%'), '_', '\_') || '%' ESCAPE '\')
  -- 通達 8-13「検索項目について記録事項がない電磁的記録を検索できる機能」。
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
-- **並び順は元帳の意味そのものである。** 科目ごとにまとまり、その中は取引日順に積み上がる。
-- 累計の窓（上の OVER 句）とこの並びが一致していないと、累計が飛び飛びに見える。
-- 年度を並び順に含める理由は仕訳帳と同じ（伝票番号は年度内の連番）。
ORDER BY a.code, e.transaction_date, e.fiscal_year_id, e.entry_no, l.line_no
