-- 振替伝票の一覧（**入力のための一覧**。ADR-0027）。**伝票 1 件が 1 レコード**。
--
-- **帳簿ではない**ので下書きを含む（[ADR-0017]）。法定検索は仕訳帳が担う。
-- 09 §3 は「帳簿は既定で絞らない・並べ替えさせない」と決めているが、
-- **この一覧はその節の対象外**である（同節の最後の項目）。
--
-- **クエリモジュールにした理由は「取り消されているか」が逆引きだから**である——
-- 取消伝票のほうが original_entry_id で原仕訳を指しており、原仕訳の行は
-- 自分が取り消されたことを知らない。列を足して UPDATE する案は採らない
-- （計上済みの更新になり trg_journal_entries_posted_no_update が拒む。ADR-0027 §2）。
--
-- パラメータは「未指定なら効かない」形で書く。CLB は空の検索欄を NULL または空文字で
-- 束縛するので、**両方を見る**（クエリモジュールの定石。_specs/QueryAndSql.md）。
--
-- 日付は date() を通して比較する。DATE 列の正規形は 'YYYY-MM-DD 00:00:00' で（qa/01 A-04）、
-- 検索欄から来る値が時刻付きとは限らないため、辞書順比較のままでは境界の 1 日が落ちうる。
SELECT
    -- 詳細（JournalEntry）へのリンクに使う。
    e.id                        AS entry_id,
    e.entry_no                  AS entry_no,
    -- **会計年度を列に出す。** 伝票番号は年度ごとの連番（I-17）なので、
    -- 年度で絞らなければ同じ「1」が何行も並ぶ。
    fy.label                    AS fiscal_year_label,
    -- **区分値は生のまま返す。** 日本語の見出しはデザイン enum が持っており
    -- （C# の列挙型・DDL の CHECK と 3 者一致を機械で検査している）、
    -- ここで CASE を書くと 4 つ目の写しになる。
    e.status                    AS status,
    e.entry_type                AS entry_type,
    e.transaction_date          AS transaction_date,
    e.posting_date              AS posting_date,
    -- **計上済みは計上時の写し、下書きは今のマスタ名**（ADR-0037）。
    -- 一覧は「いま探す」ための道具だが、**同じ伝票の詳細と名前が食い違うと、
    -- どちらが正典か分からなくなる**（開発者の指摘。2026-09-02）。
    -- 写しが無いのは取引先の無い伝票と、この列より前に計上された伝票である。
    -- **空文字も「無い」として扱う**（`NULLIF`）。帳簿の空値検索が同じ見方をしており
    -- （`JournalBook.Query.sql`）、片方だけ素通しにすると、同じ行が一覧では空欄・帳簿では現在名になる。
    COALESCE(NULLIF(e.partner_name_snapshot, ''), p.name) AS partner_name,
    e.description               AS description,
    -- 借方合計。**貸借は一致している**（I-01）ので、片側だけ出せば伝票の大きさが分かる。
    -- 下書きは一致していないことがあるが、そのときも「いま入っている借方の合計」で正しい。
    (SELECT COALESCE(SUM(l.amount), 0) FROM journal_lines l
      WHERE l.journal_entry_id = e.id AND l.debit_credit = 'debit') AS debit_total,
    -- **取消・訂正の逆引き**（ADR-0027 §2。開発者の要求はここ）。
    -- **スカラー副問い合わせで引く。JOIN しない**——JOIN は行を増やしうるが、
    -- 副問い合わせは必ず 1 値である。一意性は ux_journal_entries_single_reversal /
    -- ux_journal_entries_single_correction（どちらも status = 'posted' の部分索引）が保証する。
    --
    -- **計上済みだけを見る。** 訂正の再計上は下書きのまま返るので（ADR-0015）、
    -- 下書きを数えると「訂正の途中で放棄した伝票」が訂正済みに見える。
    -- 途中で放棄して取消だけが残るのは**正当な状態**である。
    CASE
        WHEN (SELECT 1 FROM journal_entries c
               WHERE c.original_entry_id = e.id
                 AND c.entry_type = 'correction' AND c.status = 'posted') IS NOT NULL
            THEN 'corrected'
        WHEN (SELECT 1 FROM journal_entries r
               WHERE r.original_entry_id = e.id
                 AND r.entry_type = 'reversal' AND r.status = 'posted') IS NOT NULL
            THEN 'reversed'
    END                         AS amendment_state,
    -- 相手の伝票。**訂正されているなら再計上のほう**を指す——利用者が見たいのは
    -- 「直したあとの伝票」だからである。取消だけなら取消伝票を指す。
    COALESCE(
        (SELECT c.id FROM journal_entries c
          WHERE c.original_entry_id = e.id
            AND c.entry_type = 'correction' AND c.status = 'posted'),
        (SELECT r.id FROM journal_entries r
          WHERE r.original_entry_id = e.id
            AND r.entry_type = 'reversal' AND r.status = 'posted')) AS amendment_entry_id,
    COALESCE(
        (SELECT c.entry_no FROM journal_entries c
          WHERE c.original_entry_id = e.id
            AND c.entry_type = 'correction' AND c.status = 'posted'),
        (SELECT r.entry_no FROM journal_entries r
          WHERE r.original_entry_id = e.id
            AND r.entry_type = 'reversal' AND r.status = 'posted')) AS amendment_entry_no
FROM journal_entries e
JOIN fiscal_years fy ON fy.id = e.fiscal_year_id
LEFT JOIN partners p ON p.id = e.partner_id
WHERE (@p_fiscal_year_id IS NULL OR @p_fiscal_year_id = ''
       OR e.fiscal_year_id = @p_fiscal_year_id)
  AND (@p_status IS NULL OR @p_status = '' OR e.status = @p_status)
  AND (@p_entry_type IS NULL OR @p_entry_type = '' OR e.entry_type = @p_entry_type)
  AND (@p_transaction_date_from IS NULL OR @p_transaction_date_from = ''
       OR date(e.transaction_date) >= date(@p_transaction_date_from))
  AND (@p_transaction_date_to IS NULL OR @p_transaction_date_to = ''
       OR date(e.transaction_date) <= date(@p_transaction_date_to))
  AND (@p_posting_date_from IS NULL OR @p_posting_date_from = ''
       OR date(e.posting_date) >= date(@p_posting_date_from))
  AND (@p_posting_date_to IS NULL OR @p_posting_date_to = ''
       OR date(e.posting_date) <= date(@p_posting_date_to))
  AND (@p_entry_no_min IS NULL OR @p_entry_no_min = '' OR e.entry_no >= @p_entry_no_min)
  AND (@p_entry_no_max IS NULL OR @p_entry_no_max = '' OR e.entry_no <= @p_entry_no_max)
  AND (@p_partner_id IS NULL OR @p_partner_id = '' OR e.partner_id = @p_partner_id)
  -- 摘要の部分一致。**利用者が打った文字をワイルドカードにしない**（仕訳帳と同じ理由）。
  -- 逃がす順序は「まず \ を、次に % と _ を」。逆にすると付けたばかりの \ をもう一度逃がす。
  AND (@p_keyword IS NULL OR @p_keyword = ''
       OR e.description LIKE
          '%' || replace(replace(replace(@p_keyword, '\', '\\'), '%', '\%'), '_', '\_') || '%' ESCAPE '\')
-- **計上日の新しい順**（入力の一覧なので、いま作っているものが上に来る）。
-- **同じ計上日の中では下書きが先**——番号がまだ無く、これから触るものだからである
-- （`e.entry_no IS NOT NULL` は下書きで 0、計上済みで 1。昇順で下書きが先に来る）。
-- **代理キーに順序の意味を持たせない**（qa/03 L-19）。ただし**最後の同着解消は別の話**である——
-- 下書きは伝票番号を持たないので、同じ計上日・同じ入力年月日（一括取込では同一秒に並ぶ）だと
-- 並びが一意に決まらず、**ページ送りで行が重複したり欠けたりする**（CLB が LIMIT/OFFSET を外側で付ける）。
-- id は「何番目に入ったか」しか言っていないので、順序の**意味**ではなく**同着の解き方**として使う。
ORDER BY date(e.posting_date) DESC,
         (e.entry_no IS NOT NULL),
         e.entry_no DESC,
         e.entered_at DESC,
         e.id DESC
