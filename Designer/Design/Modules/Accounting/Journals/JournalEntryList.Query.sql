-- 振替伝票の一覧（**入力のための一覧**。ADR-0027）。**伝票 1 件が 1 レコード**。
--
-- **帳簿ではない**ので下書きを含む（[ADR-0017]）。法定検索は仕訳帳が担う。
-- 21 §3 は「帳簿は既定で絞らない・並べ替えさせない」と決めているが、
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
    -- **入力年月日を列に出す**（旧 Q-22 の決定。2026-09-16）。
    -- 並びは元からこの列の降順だが（下の並べ替え）、列に出していなかったので
    -- **同じ日に複製した下書きが一覧のどの列でも見分けられなかった**（ADR-0048 の帰結）。
    -- **時分まで出す**（書式は画面の側。21 §2-5・qa/01 D-40）。
    -- **同じ分に 2 回複製したら、また見分けられない**——書式は 21 §2-5 が固定していて広げられない。
    -- そこまで起きたら摘要で見分ける（ADR-0048 の観察と同じ）。
    e.entered_at                AS entered_at,
    -- **計上済みは計上時の写し、下書きは今のマスタ名**（ADR-0037）。
    -- 一覧は「いま探す」ための道具だが、**同じ伝票の詳細と名前が食い違うと、
    -- どちらが正典か分からなくなる**（開発者の指摘。2026-09-02）。
    -- 写しが無いのは取引先の無い伝票と、この列より前に計上された伝票である。
    -- **空文字も「無い」として扱う**（`NULLIF`）。帳簿の空値検索が同じ見方をしており
    -- （`JournalBook.Query.sql`）、片方だけ素通しにすると、同じ行が一覧では空欄・帳簿では現在名になる。
    --
    -- **見るのは実効値である**（`JournalEntry.PartnerOf`。明細が空なら伝票のもの。ADR-0062）。
    -- **伝票の取引先を空にして明細だけで選んだ伝票**が正規の形になったので、
    -- 伝票の列だけを見ると**その伝票の取引先が一覧から消える**（2026-09-16 の自己レビュー。
    -- 実機で作った伝票番号 54 がまさにその形だった）。
    -- **行ごとに相手方が違うときは「（複数）」と出す**——名前を 1 つ選ぶと、選ばなかったほうが嘘になる。
    -- **括弧で括るのは、取引先の名前と見分けが付くようにするため**である（一覧の「(0件)」と同じ作法）。
    -- **数えるのは識別子**（`COALESCE(l.partner_id, e.partner_id)`）で、**出すのは名前**である——
    -- 改名した相手と改名前の写しが同じ伝票に並ぶと、名前で数えれば「複数」に化ける。
    -- **取引先の無い行は数えない**（`COUNT(DISTINCT ...)` は NULL を数えない）。
    -- 「1 相手 ＋ 取引先の無い行」の伝票は**その 1 相手の名前**を出す——
    -- **その伝票に記された相手方は 1 つだけ**であり、「複数」と言うほうが嘘になるからである。
    -- **この列でだけ日本語を作っている。** 上の「区分値は生のまま返す」に対する例外で、
    -- 「複数」には**デザイン enum の居場所が無い**（区分値ではなく、行を畳んだ要約である）。
    -- **規則の現在形は 21 §3 が持つ**（ここは実装の理由だけを持つ）。
    -- **明細を 1 行も持たない下書き**は実効値を持てないので、そのときだけ伝票の取引先を出す。
    COALESCE(
        (SELECT CASE
                    WHEN COUNT(DISTINCT COALESCE(l.partner_id, e.partner_id)) > 1 THEN '（複数）'
                    ELSE MAX(COALESCE(NULLIF(l.partner_name_snapshot, ''), lp.name,
                                      NULLIF(e.partner_name_snapshot, ''), p.name))
                END
           FROM journal_lines l
           LEFT JOIN partners lp ON lp.id = l.partner_id
          WHERE l.journal_entry_id = e.id),
        NULLIF(e.partner_name_snapshot, ''), p.name) AS partner_name,
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
  -- **取引先も実効値で探す**（ADR-0062）。伝票の列だけを見ると、
  -- **明細だけで取引先を選んだ伝票が検索から落ちる**。
  -- **明細を 1 行も持たない下書き**は実効値を持てないので、伝票の取引先で拾う。
  --
  -- **`COALESCE(l.partner_id, e.partner_id) = @p_partner_id` と書かない。**
  -- CLB は識別子を**文字列で束縛する**ので、
  -- **列と比べれば列の親和性で数に直るが、式と比べると直らず、1 件も当たらなくなる**
  -- （2026-09-16 に実際に踏んだ。`COALESCE(l.partner_id, e.partner_id) = @p_partner_id` で全滅した）。
  -- **比べる相手を列のままにする**ために、実効値を条件の側でほどく。
  AND (@p_partner_id IS NULL OR @p_partner_id = ''
       OR EXISTS (SELECT 1 FROM journal_lines l
                   WHERE l.journal_entry_id = e.id
                     AND (l.partner_id = @p_partner_id
                          OR (l.partner_id IS NULL AND e.partner_id = @p_partner_id)))
       OR (e.partner_id = @p_partner_id
           AND NOT EXISTS (SELECT 1 FROM journal_lines l WHERE l.journal_entry_id = e.id)))
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
