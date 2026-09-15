namespace BusinessApp.Schema.Tests;

using System.Globalization;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 振替伝票の一覧（入力の一覧。ADR-0027）を<b>本物の DDL に本物の SQL を流して</b>読み戻す。
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は宣言と SQL の整合しか見ない。しかも突き合わせているのは
/// <b>列の別名だけ</b>なので、<c>COALESCE(NULLIF(...), ...)</c> の 3 段のどこを取り違えても緑になる。
/// <b>行を入れて読み戻さないと分からない</b>（qa/03 の L-12・L-19・L-20）。</para>
/// <para><b>2026-09-14 までは、取引先名の出し方 3 本しか無かった。</b>
/// SQL ミューテーションの掃引（ADR-0056）で<b>80 点中 56 点が生き残り</b>、
/// <b>11 本あるパラメータを 1 つも撃っていない</b>ことが分かった（qa/03 の L-47）。</para>
/// <para>ここが守るのは <see href="../../../docs/decisions/0037-計上済みの伝票は画面でも計上時の姿を見せる.md">ADR-0037</see> §3
/// （<b>同じ伝票を一覧と詳細で見て名前が違わないこと</b>）と、
/// <see href="../../../docs/decisions/0027-取消・訂正の逆引きは一覧のクエリで解く.md">ADR-0027</see> §2
/// （<b>取消・訂正の逆引き</b>。開発者の要求はここ）である。</para>
/// </remarks>
[Collection(QuerySqlCollection.Name)]
public class JournalEntryListQueryTests
{
    /// <summary>クエリモジュールの名前。<b>SQL の読み込みと、変異の当て先の両方が指す。</b></summary>
    private const string Module = "JournalEntryList";

    /// <summary>
    /// 会計年度 2 本・伝票 10 本。
    /// </summary>
    /// <remarks>
    /// <para><b>軸を独立させてある。</b> 掃引で生き残った 56 点は、
    /// <b>どれか 2 つの軸が同じ値だと区別が付かない</b>形で残っていた。</para>
    /// <list type="bullet">
    /// <item><b>取引日と計上日をずらす。</b> しかも<b>取引日の順と計上日の順を一致させない</b>——
    /// 同じ日にすると、WHERE の 4 本の日付条件で<b>列を取り違えても誰も気づかない</b></item>
    /// <item><b>会計年度を 2 本にし、伝票番号を年度をまたいで重複させる</b>（どちらも 1 番がある）。
    /// 番号だけで絞れば 2 本、年度と組み合わせれば 1 本になる（I-17）</item>
    /// <item><b>識別子の順と年代の順を逆にする</b>——後から入れる第 17 期のほうが古い（qa/03 の L-19）</item>
    /// <item><b>状態は 6 対 4、種別は 4 通り。</b> <b>半々にしない</b>——
    /// 条件を反転しても件数が一致する置き方を避ける（qa/03 の L-46）</item>
    /// <item><b>摘要は 10 本すべて別の文字列で、1 本は NULL。</b>
    /// <c>%</c>・<c>_</c>・<c>\</c> を含むものを混ぜてある（逃がしの 5 通りの壊し方を撃つため）</item>
    /// <item><b>並びの同着を 4 段ぜんぶ作る</b>（計上日 → 下書きが先 → 伝票番号の降順 →
    /// 入力年月日の降順 → 識別子の降順）。<b>入力年月日の順と識別子の順はわざと逆</b>にしてある</item>
    /// <item><b>借方合計は 1 本だけ明細 2 行</b>（300 ＋ 700）。
    /// <c>debit_credit = 'debit'</c> の条件を落とすと 2000 になる</item>
    /// </list>
    /// <para><b>同じ額が 2 組ある</b>（1 と 4、2 と 10）。**取消は原仕訳と同額**という業務の形で、
    /// <b>意図してそろえてある</b>——それ以外の額は全部違う。</para>
    /// </remarks>
    private const string Entries = """
        INSERT INTO partners (code, name) VALUES ('P002', 'いまのマスタ名');
        -- **2 人目**。明細だけが取引先を持つ形と、行ごとに相手方が違う形（ADR-0062）に使う。
        INSERT INTO partners (id, code, name) VALUES (3, 'P003', 'もう一方のマスタ名');
        INSERT INTO fiscal_years (code, label, start_date, end_date, status, premium_ledger_from)
            VALUES ('FY17', '第 17 期（2025 年度）', '2025-04-01', '2026-03-31', 'closed', '2025-04-01');

        -- 1) 第 18 期 / 取引 05-10 / 計上 05-12 / 取引先あり（写しあり） / 1,100 円
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, partner_id, partner_name_snapshot, entered_at)
            VALUES (1, 1, '2026-05-10', '2026-05-12', 'draft', 'normal', '5 月の売上', 2, '計上したときの名前', '2026-05-12 10:00:00');
        -- **2 行とも伝票の取引先（2 番）を引き継ぐ**——実効値は 1 つなので名前が出る。
        -- `DISTINCT` を落とす変異は、ここでしか死なない（行数で数えると「複数」に化ける）。
        -- **貸方にしてあるので借方合計は 1,100 のまま**。
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 1, 'debit', 1, 1100, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 2, 'credit', 1, 1100, 1);

        -- 2) 取引 05-20 / 計上 05-25 / 写しが空文字 / 摘要に % / 5,000 円
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, partner_id, partner_name_snapshot, entered_at)
            VALUES (2, 1, '2026-05-20', '2026-05-25', 'draft', 'normal', '値引 10%', 2, '', '2026-05-25 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 1, 'debit', 1, 5000, 1);

        -- 3) 下書き / 取引 05-15 / 計上 05-27 / 写しなし / 摘要に _ / 3,000 円
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, partner_id, entered_at)
            VALUES (3, 1, '2026-05-15', '2026-05-27', 'draft', 'normal', '下書き_A', 2, '2026-05-27 12:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 1, 'debit', 1, 3000, 1);

        -- 4) 1 番の取消（計上済み）/ 取引 05-21 / 計上 05-26 / 原仕訳と同額
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, original_entry_id, entered_at)
            VALUES (4, 1, '2026-05-21', '2026-05-26', 'draft', 'reversal', '取消：5 月の売上', 1, '2026-05-26 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (4, 1, 'debit', 1, 1100, 1);

        -- 5) 2 番の訂正（計上済み）/ 取引 05-22 / 計上 05-27 / 5,200 円
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, original_entry_id, entered_at)
            VALUES (5, 1, '2026-05-22', '2026-05-27 00:00:00', 'draft', 'correction', '訂正：値引', 2, '2026-05-27 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (5, 1, 'debit', 1, 5200, 1);

        -- 6) **第 17 期**（後から入れるが年代は古い）/ 伝票番号は 1 番（年度をまたいで重複）
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, partner_id, partner_name_snapshot, entered_at)
            VALUES (6, 2, '2026-03-10', '2026-03-12', 'draft', 'normal', '3 月の売上', 2, '前期の名前', '2026-03-12 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (6, 1, 'debit', 1, 7000, 1);

        -- 8) 期首残高（normal でも取消・訂正でもない種別）/ 摘要に \ と % と _
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            VALUES (8, 1, '2026-05-24', '2026-05-28', 'draft', 'opening', '期首 T5\0%_残高', '2026-05-28 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 1, 'debit', 1, 600, 1);

        -- 7) **下書きの**取消（**8 番を指す**）/ 計上 05-27 / 入力年月日は 3 番と同じ
        --    **8 番を指す取消はこの 1 本だけ**なので、8 番は「下書きしか指していない伝票」になる
        --    ——`c.status = 'posted'` を落としたときに初めて答えが変わる唯一の行である。
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, original_entry_id, entered_at)
            VALUES (7, 1, '2026-05-23', '2026-05-27', 'draft', 'reversal', '取消の下書き', 8, '2026-05-27 12:00:00');
        -- **伝票の取引先は空で、明細だけが持つ**（ADR-0062 が正規にした形。一覧はこれを実効値で出す）。
        -- **1 行目だけが相手方を持ち、2 行目は伝票にも明細にも取引先が無い**——
        -- **実機で作った伝票番号 54 がこの形**である（2026-09-16）。
        -- **取引先の無い行は数えない**ので「複数」にはならず、1 相手の名前が出る。
        -- **貸方にしてあるので借方合計は 900 のまま**。
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, partner_id)
            VALUES (7, 1, 'debit', 1, 900, 1, 3);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 2, 'credit', 1, 900, 1);

        -- 9) 下書き / **摘要が NULL** / **借方が 2 行**（300 ＋ 700）/ 計上 05-27 / 入力年月日が最も古い
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at, partner_id)
            VALUES (9, 1, '2026-05-18', '2026-05-27', 'draft', 'normal', '2026-05-27 08:00:00', 2);
        -- **伝票の取引先（2 番）を、2 行目だけが 3 番で上書きする**（ADR-0062）。
        -- 実効値は 2 番と 3 番の 2 つなので、一覧の取引先は「複数」になる。
        -- **上書きしない行を数えないと答えが変わる**——`LEFT JOIN` を内部結合にする変異は、ここでしか死なない。
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (9, 1, 'debit', 1, 300, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, partner_id)
            VALUES (9, 2, 'debit', 2, 700, 1, 3);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (9, 3, 'credit', 2, 1000, 1);

        -- 10) 2 番の取消（計上済み）/ 取引 05-19 / 計上 05-28 / 原仕訳と同額
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, original_entry_id, entered_at)
            VALUES (10, 1, '2026-05-19', '2026-05-28', 'draft', 'reversal', '取消：値引', 2, '2026-05-28 09:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (10, 1, 'debit', 1, 5000, 1);

        -- 11) **明細を 1 行も持たない下書き**（取引先だけ選んで保存した状態）。
        --     **実効値を持てないので、伝票の取引先で拾う**という枝はここでしか死なない。
        --     **第 17 期・いちばん古い並び**にして、他の検体の期待を動かさない位置に置いた。
        INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, partner_id, entered_at)
            VALUES (11, 2, '2026-03-05', '2026-03-11', 'draft', 'normal', 3, '2026-03-11 09:00:00');

        -- 計上する。**下書きのまま残すのは 3・7・9・11 の 4 本**。
        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-12 11:00:00' WHERE id = 1;
        UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-25 11:00:00' WHERE id = 2;
        UPDATE journal_entries SET status = 'posted', entry_no = 3, posted_at = '2026-05-26 11:00:00' WHERE id = 4;
        UPDATE journal_entries SET status = 'posted', entry_no = 4, posted_at = '2026-05-27 11:00:00' WHERE id = 5;
        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-03-12 11:00:00' WHERE id = 6;
        UPDATE journal_entries SET status = 'posted', entry_no = 5, posted_at = '2026-05-28 11:00:00' WHERE id = 8;
        UPDATE journal_entries SET status = 'posted', entry_no = 6, posted_at = '2026-05-28 12:00:00' WHERE id = 10;
        """;

    /// <summary>絞り込みをかけない全 10 本の並び。</summary>
    private const string AllInOrder = "10 8 7 3 9 5 4 2 1 6 11";

    // --- 並び ---

    /// <summary>
    /// <b>計上日の新しい順 → 下書きが先 → 伝票番号の降順 → 入力年月日の降順 → 識別子の降順。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>5 段ぜんぶに同着を作ってある。</b> 1 段でも欠けると、
    /// <b>その段を壊しても答えが変わらない</b>——掃引はそれを「生き残り」と報告する。</para>
    /// <list type="bullet">
    /// <item>05-28 に計上済みが 2 本（8 と 10）→ <b>伝票番号の降順</b>が決める</item>
    /// <item>05-27 に下書き 3 本と計上済み 1 本 → <b>下書きが先</b>（<c>entry_no IS NOT NULL</c> の昇順）</item>
    /// <item>その下書き 3 本のうち 2 本は入力年月日が同じ → <b>識別子の降順</b>が決める</item>
    /// <item>残る 1 本は入力年月日が古い → <b>入力年月日の降順</b>が決める</item>
    /// </list>
    /// </remarks>
    [Fact]
    public void 計上日の新しい順に並び_同じ日では下書きが先に来る()
    {
        using var db = Create();

        Assert.Equal(AllInOrder, Joined(Run(db)));
    }

    /// <summary>
    /// <b>並びは計上日で決まる。取引日ではない。</b>
    /// </summary>
    /// <remarks>
    /// 検体は<b>取引日の順と計上日の順が一致していない</b>ので、
    /// <c>ORDER BY</c> の列を取り違えると答えが変わる。
    /// </remarks>
    [Fact]
    public void 取引日の順ではない()
    {
        using var db = Create();

        // 取引日の新しい順に並べたなら 8(05-24) が先頭に来るが、計上日では 10(05-28) が先頭。
        Assert.StartsWith("10 8", Joined(Run(db)), StringComparison.Ordinal);
    }

    // --- 絞り込み（11 本のパラメータ） ---

    /// <summary>会計年度で絞れる。</summary>
    /// <remarks>
    /// <b>伝票番号は年度ごとの連番</b>（I-17）なので、
    /// <b>年度で絞らなければ同じ番号が複数の年度から出る</b>。
    /// </remarks>
    [Fact]
    public void 会計年度で絞れる()
    {
        using var db = Create();

        Assert.Equal("6 11", Joined(Run(db, ("@p_fiscal_year_id", 2L))));
        Assert.Equal("10 8 7 3 9 5 4 2 1", Joined(Run(db, ("@p_fiscal_year_id", 1L))));
    }

    [Theory]
    [InlineData("posted", "10 8 5 4 2 1 6")]
    [InlineData("draft", "7 3 9 11")]
    public void 状態で絞れる(string status, string expected)
    {
        using var db = Create();

        Assert.Equal(expected, Joined(Run(db, ("@p_status", status))));
    }

    [Theory]
    [InlineData("normal", "3 9 2 1 6 11")]
    [InlineData("reversal", "10 7 4")]
    [InlineData("correction", "5")]
    [InlineData("opening", "8")]
    public void 伝票の種別で絞れる(string entryType, string expected)
    {
        using var db = Create();

        Assert.Equal(expected, Joined(Run(db, ("@p_entry_type", entryType))));
    }

    /// <summary>取引年月日の範囲は両端を含む。</summary>
    [Theory]
    [InlineData("2026-05-20", null, "8 7 5 4 2")]
    [InlineData("2026-05-21", null, "8 7 5 4")]
    [InlineData(null, "2026-05-15", "3 1 6 11")]
    [InlineData(null, "2026-05-14", "1 6 11")]
    [InlineData("2026-05-18", "2026-05-20", "10 9 2")]
    public void 取引年月日の範囲は両端を含む(string? from, string? to, string expected)
    {
        using var db = Create();

        Assert.Equal(
            expected,
            Joined(Run(db, ("@p_transaction_date_from", from), ("@p_transaction_date_to", to))));
    }

    /// <summary>
    /// 計上日の範囲は両端を含む。<b>取引日の範囲とは別の答えになる</b>。
    /// </summary>
    /// <remarks>
    /// <b>2 つの範囲が同じ答えを返す検体だと、条件の列を取り違えても分からない。</b>
    /// </remarks>
    [Theory]
    [InlineData("2026-05-27", null, "10 8 7 3 9 5")]
    [InlineData("2026-05-28", null, "10 8")]
    [InlineData(null, "2026-05-26", "4 2 1 6 11")]
    [InlineData(null, "2026-05-25", "2 1 6 11")]
    public void 計上日の範囲は両端を含む(string? from, string? to, string expected)
    {
        using var db = Create();

        Assert.Equal(
            expected,
            Joined(Run(db, ("@p_posting_date_from", from), ("@p_posting_date_to", to))));
    }

    /// <summary>
    /// <b>日付は列にも検索値にも時刻が付いていて構わない。</b>
    /// </summary>
    /// <remarks>
    /// <para>DATE 列の正規形は <c>'YYYY-MM-DD 00:00:00'</c>（qa/01 の A-04）だが、
    /// <b>保存された値も検索欄から来る値も、時刻が付いているとは限らない</b>。
    /// <b>4 通りの組み合わせを全部通す</b>——片側にしか <c>date()</c> が無いと、
    /// <b>その組み合わせのときだけ境界の 1 日が落ちる</b>。</para>
    /// <para><b>2 つの向きが逆に効く。</b> 列に時刻が付いていると<b>終わりの境界</b>が落ち、
    /// 検索値に時刻が付いていると<b>始まりの境界</b>が落ちる（ADR-0055 の実測で証明した）。</para>
    /// </remarks>
    [Theory]
    [InlineData("2026-05-16", "2026-05-16", "2026-05-16")]
    [InlineData("2026-05-16 00:00:00", "2026-05-16", "2026-05-16")]
    [InlineData("2026-05-16", "2026-05-16 00:00:00", "2026-05-16 00:00:00")]
    [InlineData("2026-05-16 00:00:00", "2026-05-16 00:00:00", "2026-05-16 00:00:00")]
    public void 取引年月日は時刻が付いていても日付として比べられる(string stored, string from, string to)
    {
        using var db = Create();
        TestDatabase.Execute(
            db, $"UPDATE journal_entries SET transaction_date = '{stored}' WHERE id = 3");

        Assert.Equal(
            "3",
            Joined(Run(db, ("@p_transaction_date_from", from), ("@p_transaction_date_to", to))));
    }

    /// <summary>計上日も同じく、列にも検索値にも時刻が付いていて構わない。</summary>
    /// <remarks>
    /// <b>動かすのは下書き（9 番）である。</b> 計上済みの伝票は
    /// <c>trg_journal_entries_posted_no_update</c> が変更を拒む（I-05）——
    /// **その守りが効いているので、検体も下書きで作る**。
    /// </remarks>
    [Theory]
    [InlineData("2026-05-29", "2026-05-29", "2026-05-29")]
    [InlineData("2026-05-29 00:00:00", "2026-05-29", "2026-05-29")]
    [InlineData("2026-05-29", "2026-05-29 00:00:00", "2026-05-29 00:00:00")]
    [InlineData("2026-05-29 00:00:00", "2026-05-29 00:00:00", "2026-05-29 00:00:00")]
    public void 計上日は時刻が付いていても日付として比べられる(string stored, string from, string to)
    {
        using var db = Create();
        TestDatabase.Execute(
            db, $"UPDATE journal_entries SET posting_date = '{stored}' WHERE id = 9");

        Assert.Equal(
            "9",
            Joined(Run(db, ("@p_posting_date_from", from), ("@p_posting_date_to", to))));
    }

    /// <summary>
    /// 伝票番号の範囲は両端を含む。<b>年度をまたぐと同じ番号が複数出る</b>。
    /// </summary>
    [Theory]
    [InlineData(1L, 1L, "1 6")]
    [InlineData(2L, 3L, "4 2")]
    [InlineData(5L, null, "10 8")]
    [InlineData(null, 2L, "2 1 6")]
    [InlineData(7L, null, "")]
    public void 伝票番号の範囲は両端を含む(long? min, long? max, string expected)
    {
        using var db = Create();

        Assert.Equal(expected, Joined(Run(db, ("@p_entry_no_min", min), ("@p_entry_no_max", max))));
    }

    /// <summary>
    /// 伝票番号と会計年度を組み合わせると 1 本に絞れる。
    /// </summary>
    /// <remarks>
    /// <b>条件どうしを <c>AND</c> で合成していることを見る。</b>
    /// <c>OR</c> に取り違えると、年度で絞ったはずの行が番号だけで出てくる。
    /// </remarks>
    [Fact]
    public void 伝票番号と会計年度を組み合わせると一本に絞れる()
    {
        using var db = Create();

        Assert.Equal(
            "6",
            Joined(Run(db, ("@p_entry_no_min", 1L), ("@p_entry_no_max", 1L), ("@p_fiscal_year_id", 2L))));
    }

    /// <summary>取引先で絞れる。<b>本番が束縛する型（文字列）でも通す</b>。</summary>
    [Theory]
    [InlineData(2L)]
    [InlineData("2")]
    public void 取引先で絞れる(object partnerId)
    {
        using var db = Create();

        // **9 番は明細だけが 2 番の取引先を持つ**（伝票の取引先は空）。実効値で探すので当たる（ADR-0062）。
        Assert.Equal("3 9 2 1 6", Joined(Run(db, ("@p_partner_id", partnerId))));
    }

    /// <summary>
    /// <b>摘要の部分一致は、打った文字をワイルドカードにしない。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>検体は「逃がしを外すと答えが変わる」形でなければ何も言っていない</b>
    /// （ADR-0012 §2 が禁じた「通るだけのテスト」。qa/02 のラウンド 91）。
    /// 下の組は、<b>逃がしを丸ごとやめる・順序を逆にする・<c>%</c> だけ素通し・
    /// <c>_</c> だけ素通し・<c>\</c> だけ素通し</b>の 5 通りを 1 つずつ殺す。</para>
    /// <para><b>摘要が NULL の伝票（9 番）は、どの検索にも出てこない</b>——
    /// <c>NULL LIKE '%…%'</c> は NULL だからである。
    /// <b>空文字の判定を落とすと、空欄で検索したときにその伝票が消える</b>。</para>
    /// </remarks>
    [Theory]
    // 打った通りの行だけが出る（`%` も `_` も `\` もただの文字）。
    [InlineData("T5\\0%_残高", "8")]
    // `%` を素通しにすると `値引 10%` の 2 番も当たる。
    [InlineData("引 10%", "2")]
    // `_` を素通しにすると `下書き_A` の 3 番も当たる。
    [InlineData("書き_A", "3")]
    // 逃がしをやめると `%` がワイルドカードになって 2 番と 8 番が出る。
    [InlineData("0%_", "8")]
    // 逃がし文字そのもの。**検体に実在する**ので 8 番だけ。
    [InlineData("\\", "8")]
    // 当たらない語。**0 件を期待するケースは、1 件だけ残るケースと対で置く**。
    [InlineData("存在しない語", "")]
    public void 摘要の部分一致は打った文字をワイルドカードにしない(string keyword, string expected)
    {
        using var db = Create();

        Assert.Equal(expected, Joined(Run(db, ("@p_keyword", keyword))));
    }

    /// <summary>
    /// 検索欄が空のときは絞り込まない。<b>NULL と空文字の両方</b>で。
    /// </summary>
    /// <remarks>
    /// CLB は空の検索欄を <b>NULL または空文字</b>で束縛する（_specs/QueryAndSql.md）。
    /// <b>片方しか見ていないと、空欄のまま検索したときに 0 件になる</b>——
    /// 画面は「該当なし」を出すので、静かな失敗になる。
    /// <b>11 本のパラメータ全部に空文字を渡す。</b>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void 検索欄が空なら絞り込まない(string? empty)
    {
        using var db = Create();

        Assert.Equal(AllInOrder, Joined(Run(db, [.. Parameters.Select(name => (name, (object?)empty))])));
    }

    // --- 列の中身 ---

    /// <summary>
    /// <b>14 列すべてを、行まるごと突き合わせる。</b>
    /// </summary>
    /// <remarks>
    /// <b>列の取り違えは、これでしか捕まらない。</b> <c>QueryModuleTests</c> が見るのは
    /// <b>別名だけ</b>なので、<c>e.posting_date AS transaction_date</c> と書いても宣言とは一致する。
    /// <b>とくに <c>amendment_entry_id</c> は画面のリンク先</b>（ADR-0027 §2）で、
    /// 取り違えると<b>別の伝票が開く</b>。
    /// </remarks>
    [Fact]
    public void 十四列がそれぞれの列の値を返す()
    {
        using var db = Create();

        var rows = Run(db).ToDictionary(row => row.EntryId);

        Assert.Equal(
            new Row(1, 1, "第 18 期（2026 年度）", "posted", "normal",
                    "2026-05-10", "2026-05-12", "2026-05-12 10:00:00", "計上したときの名前",
                    "5 月の売上", 1100, "reversed", 4, 3),
            rows[1]);
        Assert.Equal(
            new Row(9, null, "第 18 期（2026 年度）", "draft", "normal",
                    "2026-05-18", "2026-05-27", "2026-05-27 08:00:00", "（複数）",
                    null, 1000, null, null, null),
            rows[9]);
        Assert.Equal(
            new Row(6, 1, "第 17 期（2025 年度）", "posted", "normal",
                    "2026-03-10", "2026-03-12", "2026-03-12 10:00:00", "前期の名前",
                    "3 月の売上", 7000, null, null, null),
            rows[6]);
    }

    /// <summary>
    /// <b>取消・訂正の逆引き</b>（ADR-0027 §2。開発者の要求はここ）。
    /// </summary>
    /// <remarks>
    /// <para><b>4 通りを 1 つの検体に揃えてある。</b></para>
    /// <list type="bullet">
    /// <item>1 番: <b>計上済みの取消だけ</b>が指す → <c>reversed</c></item>
    /// <item>2 番: <b>計上済みの取消と計上済みの訂正の両方</b>が指す → <c>corrected</c>
    /// （<b>訂正が勝つ</b>。利用者が見たいのは「直したあとの伝票」だから）</item>
    /// <item>8 番: <b>下書きの取消（7 番）だけ</b>が指す → <c>null</c>
    /// （訂正の途中で放棄した伝票が訂正済みに見えないように。ADR-0015）。
    /// <b>ここが「下書きを数えない」を表明する唯一の行である</b></item>
    /// <item>6 番: 誰も指さない → <c>null</c></item>
    /// </list>
    /// </remarks>
    [Fact]
    public void 取消と訂正を逆引きできる()
    {
        using var db = Create();

        var rows = Run(db).ToDictionary(row => row.EntryId);

        Assert.Equal(("reversed", 4L, 3L), (rows[1].AmendmentState, rows[1].AmendmentEntryId, rows[1].AmendmentEntryNo));
        Assert.Equal(("corrected", 5L, 4L), (rows[2].AmendmentState, rows[2].AmendmentEntryId, rows[2].AmendmentEntryNo));
        Assert.Null(rows[6].AmendmentState);
        Assert.Null(rows[8].AmendmentState);
    }

    /// <summary>
    /// <b>下書きの取消は数えない。計上したら数える。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>8 番を指す取消は、下書きの 7 番 1 本だけ</b>である。
    /// <c>r.status = 'posted'</c> を落とすと、**8 番が取消済みに見える**
    /// ——**訂正の途中で放棄した伝票が「直した」ように見える**（ADR-0015）。</para>
    /// <para><b>同じ検体で逆向きも見る</b>——7 番を計上すると 8 番は取消済みになる。
    /// **片側だけだと「いつも null を返す」実装でも緑になる**。</para>
    /// </remarks>
    [Fact]
    public void 下書きの取消は数えず計上したら数える()
    {
        using var db = Create();

        Assert.Null(Run(db).Single(row => row.EntryId == 8).AmendmentState);

        TestDatabase.Execute(
            db,
            "UPDATE journal_entries SET status = 'posted', entry_no = 7, posted_at = '2026-05-27 13:00:00' WHERE id = 7");

        var reversed = Run(db).Single(row => row.EntryId == 8);

        Assert.Equal(("reversed", 7L, 7L), (reversed.AmendmentState, reversed.AmendmentEntryId, reversed.AmendmentEntryNo));
    }

    /// <summary>
    /// <b>借方合計は借方の明細だけを足す。</b>
    /// </summary>
    /// <remarks>
    /// 9 番は借方 2 行（300 ＋ 700）と貸方 1 行（1,000）を持つ。
    /// <c>debit_credit = 'debit'</c> を落とすと 2,000 になる。
    /// </remarks>
    [Fact]
    public void 借方合計は借方の明細だけを足す()
    {
        using var db = Create();

        Assert.Equal(1000, Run(db).Single(row => row.EntryId == 9).DebitTotal);
    }

    // --- 取引先名（写しと現在名。ADR-0037 §3）---

    [Fact]
    public void 計上済みは写しを出し_下書きは現在のマスタ名を出す()
    {
        using var db = Create();

        var rows = Run(db).ToDictionary(row => row.EntryId, row => row.PartnerName);

        Assert.Equal("計上したときの名前", rows[1]);

        // **空文字は「無い」として扱う**（NULLIF）。帳簿の空値検索と同じ見方に揃えてある——
        // 素通しにすると、同じ行が一覧では空欄・帳簿では現在名になる。
        Assert.Equal("いまのマスタ名", rows[2]);

        // 下書きはまだ写しを持たない。入力の途中なので、いま選べる名前を出すのが正しい。
        Assert.Equal("いまのマスタ名", rows[3]);
    }

    [Fact]
    public void 取引先を改名しても計上済みの行は動かない()
    {
        using var db = Create();
        TestDatabase.Execute(db, "UPDATE partners SET name = '改名したあとの名前' WHERE id = 2");

        var rows = Run(db).ToDictionary(row => row.EntryId, row => row.PartnerName);

        Assert.Equal("計上したときの名前", rows[1]);
        Assert.Equal("改名したあとの名前", rows[3]);
    }

    [Fact]
    public void 取引先の無い伝票は空のまま()
    {
        using var db = Create();

        Assert.Null(Run(db).Single(row => row.EntryId == 8).PartnerName);
    }

    /// <summary>
    /// <b>伝票の取引先が空で、明細だけが持つ伝票も、名前が出る</b>（ADR-0062）。
    /// </summary>
    /// <remarks>
    /// <b>ADR-0062 が正規にした形である。</b> 伝票の列だけを見ていると、
    /// <b>この伝票の取引先が一覧から消える</b>（2026-09-16 の自己レビューで見つけた。
    /// 実機で作った伝票がまさにこの形だった）。
    /// </remarks>
    [Fact]
    public void 明細だけが取引先を持つ伝票も名前が出る()
    {
        using var db = Create();

        // 7 番は 1 行目だけが相手方を持ち、2 行目は伝票にも明細にも取引先が無い。
        // **取引先の無い行は数えない**ので「（複数）」にはならない（実機の伝票番号 54 と同じ形）。
        Assert.Equal("もう一方のマスタ名", Run(db).Single(row => row.EntryId == 7).PartnerName);
    }

    /// <summary>
    /// <b>行ごとに相手方が違うときは「（複数）」と出す</b>（ADR-0062）。
    /// </summary>
    /// <remarks>
    /// 名前を 1 つ選ぶと、<b>選ばなかったほうが嘘になる</b>。
    /// <b>括弧で括るのは、取引先の名前と見分けが付くようにするため</b>である。
    /// <b>数えるのは識別子で、出すのは名前である</b>——名前で数えると、
    /// 改名した相手と改名前の写しが同じ伝票に並んだだけで「複数」に化ける。
    /// </remarks>
    [Fact]
    public void 行ごとに相手方が違えば括弧つきの複数と出す()
    {
        using var db = Create();

        Assert.Equal("（複数）", Run(db).Single(row => row.EntryId == 9).PartnerName);
    }

    /// <summary>
    /// <b>明細だけが持つ取引先でも絞れる</b>（ADR-0062）。
    /// </summary>
    /// <remarks>
    /// <b>本番が束縛する型（文字列）でも通す</b>——<c>COALESCE(...) = @p</c> と書くと
    /// <b>列の親和性が効かず 1 件も当たらない</b>（2026-09-16 に実際に踏んだ）。
    /// </remarks>
    [Theory]
    [InlineData(3)]
    [InlineData("3")]
    public void 明細だけが持つ取引先でも絞れる(object partnerId)
    {
        using var db = Create();

        // 7 番は明細だけが 3 番を持ち、9 番は 2 行目が 3 番を持つ（1 行目は 2 番）。
        Assert.Equal("7 9 11", Joined(Run(db, ("@p_partner_id", partnerId))));
    }

    private static SqliteConnection Create()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, Entries);
        return db;
    }

    /// <summary>
    /// 返った行を <c>entry_id</c> の並びにする。
    /// </summary>
    /// <remarks>
    /// <b>件数ではなく、どの行がどの順で返ったかを表明するため</b>にある（qa/03 の L-46）。
    /// <b>並びも一緒に固定される</b>ので、<c>ORDER BY</c> の取り違えもここで落ちる。
    /// </remarks>
    private static string Joined(IEnumerable<Row> rows)
        => string.Join(" ", rows.Select(row => row.EntryId.ToString(CultureInfo.InvariantCulture)));

    /// <summary>SQL が返す 14 列。<b>1 つも省かない</b>（省いた列は誰も見ていないことになる）。</summary>
    private sealed record Row(
        long EntryId, long? EntryNo, string FiscalYearLabel, string Status, string EntryType,
        string TransactionDate, string PostingDate, string EnteredAt, string? PartnerName,
        string? Description, long DebitTotal, string? AmendmentState,
        long? AmendmentEntryId, long? AmendmentEntryNo);

    /// <summary>一覧の SQL を<b>本物のまま</b>流す。渡さなかったパラメータは NULL（＝条件なし）。</summary>
    /// <remarks>
    /// <b>知らないパラメータ名を黙って捨てない。</b> 綴りを誤ると
    /// <b>その条件が無いものとして流れ、「絞り込まれないこと」を見るテストが間違った理由で緑になる</b>。
    /// </remarks>
    private static IReadOnlyList<Row> Run(
        SqliteConnection db, params (string Name, object? Value)[] parameters)
    {
        var unknown = parameters.Select(p => p.Name).Where(name => !Parameters.Contains(name)).ToList();
        Assert.True(
            unknown.Count == 0,
            $"この SQL に無いパラメータを渡している: {string.Join(" / ", unknown)}");

        using var command = db.CreateCommand();
        command.CommandText = TestDatabase.QuerySql(Module);

        foreach (var name in Parameters)
        {
            var (_, value) = parameters.FirstOrDefault(p => p.Name == name);
            command.Parameters.AddWithValue(name, value ?? (object)DBNull.Value);
        }

        using var reader = SqlMutationProbe.ExecuteReader(command, Module);
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt64(reader.GetOrdinal("entry_id")),
                Number(reader, "entry_no"),
                reader.GetString(reader.GetOrdinal("fiscal_year_label")),
                reader.GetString(reader.GetOrdinal("status")),
                reader.GetString(reader.GetOrdinal("entry_type")),
                reader.GetString(reader.GetOrdinal("transaction_date")),
                reader.GetString(reader.GetOrdinal("posting_date")),
                reader.GetString(reader.GetOrdinal("entered_at")),
                Text(reader, "partner_name"),
                Text(reader, "description"),
                reader.GetInt64(reader.GetOrdinal("debit_total")),
                Text(reader, "amendment_state"),
                Number(reader, "amendment_entry_id"),
                Number(reader, "amendment_entry_no")));
        }

        return rows;
    }

    private static string? Text(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static long? Number(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);

        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    /// <summary>SQL が使う入力パラメータ。<b>足りないと SQLite が実行時に落ちる</b>。</summary>
    private static readonly string[] Parameters =
    [
        "@p_fiscal_year_id", "@p_status", "@p_entry_type",
        "@p_transaction_date_from", "@p_transaction_date_to",
        "@p_posting_date_from", "@p_posting_date_to",
        "@p_entry_no_min", "@p_entry_no_max", "@p_partner_id", "@p_keyword",
    ];
}
