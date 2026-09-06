-- 総勘定元帳。**明細 1 行が 1 レコード**（仕訳帳と同じ形。ADR-0022）。
--
-- 帳簿なので**計上済みだけ**を出す。下書きは帳簿ではない（docs/10 §5）。
--
-- **勘定科目ごとに並べ、勘定科目ごとに累計する。** 勘定科目を指定しなければ全科目が科目コード順に
-- 並ぶ（それが「総勘定元帳」そのものである）。1 科目に絞れば 1 科目の元帳、
-- 補助科目・取引先まで絞れば補助元帳になる（法令が名前を与えている帳簿は仕訳帳と総勘定元帳だけ。
-- ADR-0017）。
--
-- 記録項目（電帳通達 8-14）:
--   総勘定元帳 = 記載年月日・取引金額／補助元帳 = 記録又は取引の年月日・取引金額・取引先名称。
--   **記載年月日は、個々の取引を転記する場合は転記した取引の取引年月日**（電帳一問一答 問 33）なので、
--   ここでは取引日で引く。取引先でも引けるようにしてあるので、補助元帳の記録項目も満たす。
--   伝票番号でも引ける——電帳通達 8-14 (注) の「一連番号等で検索できるとき」の経路である。
--
-- パラメータは「未指定なら効かない」形で書く。CLB は空の検索欄を NULL または空文字で束縛するので、
-- **両方を見る**（仕訳帳と同じ定石）。
SELECT
    e.id                        AS entry_id,
    e.entry_no                  AS entry_no,
    a.code                      AS account_code,
    a.name                      AS account_name,
    sa.name                     AS sub_account_name,
    -- **会計年度を列に出す。** 累計を年度で切る以上（下の OVER 句）、
    -- 出さないと利用者には「累計が突然戻る」「取引日が遡る」理由が見えない。
    -- 帳簿は既定で絞らない（docs/21 §3）ので、既定の表示は必ず年度が混ざる。
    fy.label                    AS fiscal_year_label,
    e.transaction_date          AS transaction_date,
    -- **区分値は生のまま返す。** 日本語の見出しは CLB のデザイン enum が持っている
    -- （C# の列挙型・DDL の CHECK と 3 者一致を機械で検査している）。
    e.entry_type                AS entry_type,
    -- **相手勘定科目**（法税規則 55 ② の法定記載事項）。
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
    -- 取引先名は**明細の写しを優先**する（docs/10 §4-2）。仕訳帳と同じ規則で引く。
    COALESCE(l.partner_name_snapshot, lp.name, ep.name) AS partner_name,
    -- 借方と貸方を別の列に出す。**片方は NULL** になり、画面では空欄になる。
    CASE WHEN l.debit_credit = 'debit'  THEN l.amount END AS debit_amount,
    CASE WHEN l.debit_credit = 'credit' THEN l.amount END AS credit_amount,
    -- **期間内累計。** 期首残高（フェーズ 4）が無いので、残高ではなく
    -- 「ここに出ている範囲の先頭からの累計」である。画面の列名もそう名づけてある。
    --
    -- **会計年度をまたいで積み上げない**（開発者の決定。2026-08-28。
    -- 理由は「損益科目の累計が決算をまたいで積み上がる」）。
    -- **その理由を損益科目に限って当て、貸借科目は切らない**（Claude の判断。2026-08-30。
    -- 現金や買掛金の残高は年度をまたいで続くもので、年度で 0 に戻すと
    -- 手許現金でも残高でもない数が出る。I-07。qa/02 の R24-03）。
    -- 区分の判定は **AccountCategory.IsProfitAndLoss の 2 つ目の実装**である（収益・費用）。
    -- **評価勘定（is_contra）は区分を変えない**——売上値引・戻り高は収益の評価勘定であって損益科目である。
    --
    -- 会計年度が 1 つのうちは表に出ないが、第 17 期のデモデータか翌期が入ると必ず出る。
    --
    -- 符号は**その科目の通常残高の側**に合わせる（docs/10 §6）。科目区分だけでは決まらない——
    -- 評価勘定（減価償却累計額・売上値引・戻り高など）は通常残高が科目区分と逆になる。
    -- 借方が通常なら「借方 − 貸方」、貸方が通常なら「貸方 − 借方」。
    -- こうしないと売上の累計が負の数で出る。
    SUM(
        CASE WHEN l.debit_credit = 'debit' THEN l.amount ELSE -l.amount END
        * CASE WHEN (a.category IN ('asset', 'expense')) <> (a.is_contra = 1) THEN 1 ELSE -1 END
    ) OVER (
        -- 貸借科目では CASE が全行 NULL になり、科目ごとの 1 つの窓になる
        -- （**PARTITION BY の式が NULL の行は 1 区画に束ねられる**。qa/01 A-09）。
        PARTITION BY l.account_id,
                     CASE WHEN a.category IN ('revenue', 'expense') THEN e.fiscal_year_id END
        -- **年度の順は開始日で決める。** fiscal_years.id は AUTOINCREMENT の代理キーで、
        -- 年代とは無関係な挿入順である——第 17 期を後から入れると id は第 18 期より大きくなる。
        ORDER BY date(fy.start_date), date(e.transaction_date), e.entry_no, l.line_no
    )                           AS running_total,
    l.item_description          AS item_description,
    e.description               AS description
FROM journal_entries e
JOIN journal_lines   l  ON l.journal_entry_id = e.id
JOIN accounts        a  ON a.id  = l.account_id
-- 会計年度は**列に出すため**と**並び順のため**に引く（識別子ではなく開始日で並べる）。
JOIN fiscal_years    fy ON fy.id = e.fiscal_year_id
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
  -- 電帳通達 8-13「検索項目について記録事項がない電磁的記録を検索できる機能」。
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
-- **並び順は元帳の意味そのものである。** 科目ごと・会計年度ごとにまとまり、
-- その中は取引日順に積み上がる。
-- **累計の窓（上の OVER 句）とこの並びは必ず一致させる。** ずれると累計が飛び飛びに見える。
-- だから年度で累計を切るなら、**並びも年度で切らなければならない**——
-- 取引日を年度より先に置くと、計上日が翌年度にずれた伝票（決算後に見つかった取引）が
-- 前年度の行の間に挟まり、そこだけ累計が別の系列の値を出す。
-- 会計期間への帰属を決めるのは計上日である（docs/10 の I-03）。
-- **年度は開始日で並べる**（id は代理キー。上の OVER 句と同じ理由）。
-- 年度の中で伝票番号を使えるのは、それが年度内の連番だからである（I-17）。
ORDER BY a.code, date(fy.start_date), date(e.transaction_date), e.entry_no, l.line_no
