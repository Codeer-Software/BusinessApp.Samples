namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;

/// <summary>
/// 仕訳帳の法定検索が<b>実際に効くこと</b>。
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は宣言と SQL の整合しか見ない。
/// <b>条件が効いているかは、値を入れて行数を数えないと分からない。</b>
/// `&gt;=` と `&gt;` の取り違え、`date()` の掛け忘れ、`p_blank_field` の分岐名の綴り違いは、
/// どれも例外にならず<b>静かに 0 件や全件</b>を返す（qa/03 の L-12・L-19・L-20）。</para>
/// <para>ここが守るのは制度要件そのものである——<b>docs/40 の D1</b>（記録項目を検索条件にできる。
/// 電帳通達 8-14）・<b>40 の D2</b>（記録事項がない記録も検索できる。電帳通達 8-13）・
/// <b>40 の D3</b>（範囲指定と 2 項目の組み合わせ。電帳規則 5 ⑤一ハの (2)(3)・
/// 電帳通達 8-15 と 8-16）。<b>範囲は 8-15、組合せは 8-16 である</b>——別の通達なので分けて書く。</para>
/// <para><b>条番号ではなく項目 ID で引く。</b> 優良な電子帳簿の要件は 2027-01-01 に条番号が動く
/// （[qa/05 §5](../../docs/qa/05_観点網羅の計器.md)）ので、<b>docs/40 の項目 ID を正とする</b>。</para>
/// </remarks>
[Collection(QuerySqlCollection.Name)]
public class JournalBookQueryTests
{
    /// <summary>クエリモジュールの名前。<b>SQL の読み込みと、変異の当て先の両方が指す。</b></summary>
    private const string Module = "JournalBook";

    /// <summary>
    /// 帳簿に載る素材。取引日・金額・取引先・部門・摘要をすべてずらしてある。
    /// </summary>
    /// <remarks>
    /// <b>区別すべきものを同じ値にしない</b>（qa/03 L-02）。取引日と計上日も必ずずらす。
    /// </remarks>
    private const string Book = """
        INSERT INTO partners (code, name) VALUES ('P002', '乙商事');
        -- 1) 05-10 取引 / 05-12 計上 / 1000 円 / 取引先あり（伝票） / 部門あり / 摘要あり
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, partner_id, entered_at)
            VALUES (1, '2026-05-10', '2026-05-12', 'draft', 'normal', '5 月の売上', 1, '2026-05-12 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id, item_description)
            VALUES (1, 1, 'debit', 1, 2, 1000, 1, '商品 A');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 2, 'credit', 2, 1000, 1);

        -- 2) 05-20 取引 / 05-25 計上 / 5000 円 / 取引先なし / 部門なし / 摘要なし
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-20', '2026-05-25', 'draft', 'normal', '2026-05-25 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 1, 'debit', 1, 5000, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 2, 'credit', 2, 5000, 1);

        -- 3) 下書き。**帳簿には出ない。**
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-15', '2026-05-15', 'draft', 'normal', '2026-05-15 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 1, 'debit', 1, 3000, 1);
        """;

    /// <summary>
    /// 下書きを計上する。<b>計上したあとは何も直せない</b>ので（I-05・DDL のトリガ）、
    /// テストごとの差異は計上より前に入れる。
    /// </summary>
    private const string Post =
        "UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-12 10:00:00' WHERE id = 1";

    /// <summary>**摘要のない**伝票 2 の計上。<b>いまの製品では作れない形</b>なので、トリガを外して作る。</summary>
    private const string PostBlankDescription =
        "UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-25 10:00:00' WHERE id = 2";

    // --- 出す行の範囲 ---

    [Fact]
    public void 条件なしなら計上済みの明細が全部出る()
    {
        using var db = Create();

        // 4 行 = 計上済み 2 伝票 × 明細 2 行。**下書きの 1 行は出ない。**
        Assert.Equal([1L, 1L, 2L, 2L], Run(db).Select(r => r.EntryId));
    }

    [Fact]
    public void 下書きは帳簿に出ない()
    {
        using var db = Create();

        Assert.DoesNotContain(3L, Run(db).Select(r => r.EntryId));
    }

    // --- 取引年月日の範囲（電帳規則 5 ⑤一ハ(2)・電帳通達 8-15）---

    [Theory]
    [InlineData("2026-05-10", "2026-05-20", 4)]   // 両端を含む
    [InlineData("2026-05-11", "2026-05-20", 2)]   // 下端の翌日 → 1 件目が落ちる
    [InlineData("2026-05-10", "2026-05-19", 2)]   // 上端の前日 → 2 件目が落ちる
    [InlineData("2026-05-20", "2026-05-20", 2)]   // 1 日だけ
    [InlineData("2026-05-21", "2026-05-31", 0)]
    public void 取引日の範囲は両端を含む(string from, string to, int expected)
    {
        // **境界の両端を必ず通す。** `>=` を `>` に取り違えても、片側だけのテストでは緑になる。
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_transaction_date_from", from), ("@p_transaction_date_to", to)).Count);
    }

    [Fact]
    public void 取引日で引いても計上日では引かない()
    {
        // 取引日 05-20 の伝票は計上日 05-25。取引日の条件が計上日を見ていたら、これが落ちる。
        using var db = Create();

        Assert.Equal([2L, 2L], Run(db, ("@p_transaction_date_from", "2026-05-16")).Select(r => r.EntryId));
    }

    // --- 取引金額の範囲（電帳規則 5 ⑤一ハ(2)・電帳通達 8-15）---

    [Theory]
    [InlineData(1000, 5000, 4)]
    [InlineData(1001, 5000, 2)]
    [InlineData(1000, 4999, 2)]
    [InlineData(5000, 5000, 2)]
    [InlineData(5001, 9999, 0)]
    public void 金額の範囲は両端を含む(long min, long max, int expected)
    {
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_amount_min", min), ("@p_amount_max", max)).Count);
    }

    // --- 伝票番号（電帳通達 8-14 (注) の一連番号による検索。docs/40 の D1）---

    [Theory]
    [InlineData(1, 2, 4)]
    [InlineData(2, 2, 2)]
    [InlineData(1, 1, 2)]
    [InlineData(3, 9, 0)]
    public void 伝票番号の範囲は両端を含む(long min, long max, int expected)
    {
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_entry_no_min", min), ("@p_entry_no_max", max)).Count);
    }

    /// <summary>
    /// 片側だけの指定も効く。<b>両方揃ったときだけ効く実装</b>だと、ここが落ちる。
    /// </summary>
    [Fact]
    public void 伝票番号は片側だけでも絞れる()
    {
        using var db = Create();

        Assert.Equal([2L, 2L], Run(db, ("@p_entry_no_min", 2L)).Select(r => r.EntryId));
        Assert.Equal([1L, 1L], Run(db, ("@p_entry_no_max", 1L)).Select(r => r.EntryId));
    }

    /// <summary>
    /// <b>docs/40 の C1</b>（個別転記——同一取引であることを示す一連番号等）。
    /// <b>伝票番号は会計年度の中の連番</b>なので、年度を指定しなければ同じ番号が複数の年度から出る。
    /// 年度と組み合わせれば 1 本に絞れる（電帳通達 8-15 の「課税期間ごとに」）。
    /// </summary>
    [Fact]
    public void 伝票番号は会計年度と組み合わせて一意になる()
    {
        using var db = Create();
        TestDatabase.Execute(db, """
            INSERT INTO fiscal_years (code, label, start_date, end_date, status)
                VALUES ('FY19', '第 19 期', '2027-04-01', '2028-03-31', 'open');
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
                VALUES ('伝票番号 1 の取消: 5 月の売上', 2, '2027-04-02', '2027-04-02', 'draft', 'reversal', 1, '2027-04-02 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (4, 1, 'credit', 1, 1000, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2027-04-02 10:00:00' WHERE id = 4;
            """);

        // 番号だけで引くと、第 18 期の 1 番（2 行）と第 19 期の 1 番（1 行）が並ぶ。
        Assert.Equal([1L, 1L, 4L], Run(db, ("@p_entry_no_min", 1L), ("@p_entry_no_max", 1L)).Select(r => r.EntryId));

        // 年度を足すと 1 本になる。
        Assert.Equal([4L], Run(db,
            ("@p_entry_no_min", 1L), ("@p_entry_no_max", 1L), ("@p_fiscal_year_id", 2L)).Select(r => r.EntryId));
    }

    // --- 組み合わせ（電帳規則 5 ⑤一ハ(3)・電帳通達 8-16）---

    [Fact]
    public void 課税期間と日付と金額を組み合わせられる()
    {
        // **範囲（8-15「課税期間ごとに、日付又は金額の任意の範囲を指定して」）と
        // 組合せ（8-16「いずれの 2 の組合せによっても」）を同時に撃つ。**
        using var db = Create();

        var rows = Run(db,
            ("@p_fiscal_year_id", 1L),
            ("@p_transaction_date_from", "2026-05-01"),
            ("@p_transaction_date_to", "2026-05-31"),
            ("@p_amount_min", 2000L));

        Assert.Equal([2L, 2L], rows.Select(r => r.EntryId));
    }

    [Fact]
    public void 別の会計年度を指定すると一件も出ない()
    {
        using var db = Create();

        Assert.Empty(Run(db, ("@p_fiscal_year_id", 99L)));
    }

    // --- 空値検索（電帳通達 8-13）---

    /// <summary>
    /// <b>返った行を名指しで表明する。件数だけ見ない。</b>
    /// </summary>
    /// <remarks>
    /// <b>件数は「どの行か」を捨てた要約である。</b> 検体は「条件を満たす行」と「満たさない行」が
    /// どちらも 2 行なので、<b>SQL の <c>IS NULL</c> を <c>IS NOT NULL</c> に反転すると
    /// 返る行がそっくり入れ替わるのに、件数は同じまま</b>——**テストは緑を返していた**
    /// （2026-09-14 に SQL ミューテーションの掃引で見つけた。qa/03 の L-46）。
    /// </remarks>
    [Theory]
    [InlineData("partner", "2-1 2-2")]                 // 取引先が無いのは 2 番の 2 行
    [InlineData("department", "1-2 2-1 2-2")]          // 部門があるのは 1 番の 1 行目だけ
    [InlineData("description", "2-1 2-2")]             // 摘要が無いのは 2 番
    [InlineData("item_description", "1-2 2-1 2-2")]    // 内容があるのは 1 番の 1 行目だけ
    [InlineData("sub_account", "1-1 1-2 2-1 2-2")]     // 補助科目はどこにも無い
    public void 記録事項がない行を探せる(string field, string expected)
    {
        // **候補値の綴りが 1 つでもずれると 0 件が静かに返る。** 5 値すべてを通す。
        using var db = Create();

        Assert.Equal(expected, Joined(Run(db, ("@p_blank_field", field))));
    }

    /// <summary>
    /// <b>勘定科目で絞れる。</b>
    /// </summary>
    /// <remarks>
    /// <b>この条件は入れた日から一度も撃たれていなかった</b>——
    /// `@p_account_id` を渡すテストが 1 本も無く、**条件を丸ごと壊しても全テストが緑**だった
    /// （2026-09-14 の掃引。qa/02 のラウンド 92）。
    /// </remarks>
    [Fact]
    public void 勘定科目で絞れる()
    {
        using var db = Create();

        Assert.Equal("1-1 2-1", Joined(Run(db, ("@p_account_id", 1L))));
        Assert.Equal("1-2 2-2", Joined(Run(db, ("@p_account_id", 2L))));
    }

    /// <summary>
    /// <b>取引年月日は、列にも検索値にも時刻が付いていて構わない。</b>
    /// </summary>
    /// <remarks>
    /// <para>DATE 列の正規形は <c>'YYYY-MM-DD 00:00:00'</c>（qa/01 の A-04）だが、
    /// <b>保存された値も検索欄から来る値も、時刻が付いているとは限らない</b>。
    /// <b>4 通りの組み合わせを全部通す</b>——片側にしか <c>date()</c> が無いと、
    /// <b>その組み合わせのときだけ境界の 1 日が落ちる</b>。</para>
    /// <para><b>2 つの向きが逆に効く。</b> 列に時刻が付いていると<b>終わりの境界</b>が落ち、
    /// 検索値に時刻が付いていると<b>始まりの境界</b>が落ちる。
    /// <b>片方だけの検体では、もう片方の <c>date()</c> を外しても緑のまま</b>だった（同じ掃引）。</para>
    /// </remarks>
    [Theory]
    [InlineData("2026-05-10", "2026-05-10", "2026-05-10")]
    [InlineData("2026-05-10 00:00:00", "2026-05-10", "2026-05-10")]
    [InlineData("2026-05-10", "2026-05-10 00:00:00", "2026-05-10 00:00:00")]
    [InlineData("2026-05-10 00:00:00", "2026-05-10 00:00:00", "2026-05-10 00:00:00")]
    public void 取引年月日は時刻が付いていても日付として比べられる(string stored, string from, string to)
    {
        // **計上したあとは直せない**ので、計上より前に入れる。
        using var db = Create($"UPDATE journal_entries SET transaction_date = '{stored}' WHERE id = 1");

        Assert.Equal(
            "1-1 1-2",
            Joined(Run(db, ("@p_transaction_date_from", from), ("@p_transaction_date_to", to))));
    }

    [Fact]
    public void 知らない空値の指定は一件も返さない()
    {
        // 候補と SQL の分岐がずれたときの症状を固定しておく（黙って全件にはしない）。
        using var db = Create();

        Assert.Empty(Run(db, ("@p_blank_field", "unknown_field")));
    }

    // --- 取引先（法定記載事項①）---

    /// <summary>取引先で引ける。<b>本番が束縛する型（文字列）でも通す</b>（qa/01 H-09）。</summary>
    /// <remarks>
    /// <b>数だけで試すと緑のまま通る。</b> CLB は識別子を文字列で束縛し、
    /// SQLite が文字列を数に直すのは<b>列と比べるときだけ</b>である——
    /// <c>COALESCE(l.partner_id, e.partner_id) = @p</c> と書いていた間は、
    /// <b>実機では 1 件も当たらなかった</b>（2026-09-16 に稼働 DB で実測して直した）。
    /// </remarks>
    [Theory]
    [InlineData(1L)]
    [InlineData("1")]
    public void 取引先は伝票の値でも明細の値でも引ける(object partnerId)
    {
        using var db = Create();

        // 1 番は伝票に取引先がある。明細には無い。
        Assert.Equal([1L, 1L], Run(db, ("@p_partner_id", partnerId)).Select(r => r.EntryId));
    }

    [Fact]
    public void 明細の取引先は伝票の取引先を上書きする()
    {
        // 表示・検索・空値検索が同じモデルであること。
        // 明細に乙商事を入れたら、帳簿の表示も検索も乙商事に従う。
        using var db = Create(
            "UPDATE journal_lines SET partner_id = 2 WHERE journal_entry_id = 1 AND line_no = 1");

        Assert.Equal("乙商事", Run(db).First(r => r.LineNo == 1).PartnerName);
        Assert.Equal([1L], Run(db, ("@p_partner_id", 2L)).Select(r => r.EntryId));
        Assert.Single(Run(db, ("@p_partner_id", 1L)));

        // **文字列でも同じ答えになる**（qa/01 H-09。本番はこちらで束縛する）。
        Assert.Equal([1L], Run(db, ("@p_partner_id", "2")).Select(r => r.EntryId));
        Assert.Single(Run(db, ("@p_partner_id", "1")));
    }

    [Fact]
    public void 取引先名は明細の写しを優先する()
    {
        // 取引先の改名で過去の帳簿の記載が変わらないこと（docs/10 §4-2）。
        using var db = Create(
            "UPDATE journal_lines SET partner_name_snapshot = '株式会社取引先（旧称）' WHERE journal_entry_id = 1 AND line_no = 1");

        Assert.Equal("株式会社取引先（旧称）", Run(db).First(r => r.LineNo == 1).PartnerName);
    }

    // --- 摘要・内容の部分一致 ---

    [Fact]
    public void 摘要でも内容でも引ける()
    {
        using var db = Create();

        Assert.Equal(2, Run(db, ("@p_keyword", "売上")).Count);       // 摘要は伝票の項目なので 2 行
        Assert.Single(Run(db, ("@p_keyword", "商品")));               // 内容は明細の項目なので 1 行
    }

    [Fact]
    public void 打った文字はワイルドカードにならない()
    {
        // 「%」だけを打つと全件に当たる、という壊れ方をしない。
        using var db = Create("UPDATE journal_entries SET description = '値引 10%' WHERE id = 2");

        Assert.Empty(Run(db, ("@p_keyword", "%%")));
        Assert.Equal(2, Run(db, ("@p_keyword", "10%")).Count);
    }

    // --- 並び順（相互関連性の読みやすさ）---

    /// <summary>
    /// 取引日 → <b>会計年度（開始日）</b> → 伝票番号 → 行番号の順。
    /// <b>docs/40 の E2</b>（見読可能性——整然とした形式で画面へ出せる）の、画面の側。
    /// </summary>
    /// <remarks>
    /// <para>先頭が取引日なのは、仕訳帳が「取引の発生順に」記載する帳簿だからである
    /// （法税規則 55 ①）。伝票番号は<b>年度内</b>の連番なので、
    /// 年度を並び順に入れないと年度をまたぐ取消が原仕訳より前に並ぶ。</para>
    /// <para><b>年度は開始日で並べる。識別子で並べない。</b> <c>fiscal_years.id</c> は
    /// <c>AUTOINCREMENT</c> の代理キーなので、後から入れた古い年度ほど id が大きくなる（qa/03 L-19）。
    /// 検体はその形を作る——第 19 期のあとに<b>第 17 期</b>を入れるので、
    /// 第 17 期は id が最大でいちばん古い。</para>
    /// </remarks>
    [Fact]
    public void 取引日_会計年度_伝票番号_行番号の順に並ぶ()
    {
        using var db = Create();
        TestDatabase.Execute(db, """
            INSERT INTO fiscal_years (code, label, start_date, end_date, status)
                VALUES ('FY19', '第 19 期', '2027-04-01', '2028-03-31', 'open');
            INSERT INTO fiscal_years (code, label, start_date, end_date, status)
                VALUES ('FY17', '第 17 期', '2025-04-01', '2026-03-31', 'open');

            -- 4) 第 19 期の取消。取引日は原仕訳と同じ 05-10、計上は翌々年度。
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
                VALUES ('伝票番号 1 の取消: 5 月の売上', (SELECT id FROM fiscal_years WHERE code = 'FY19'),
                        '2026-05-10', '2027-04-02', 'draft', 'reversal', 1, '2027-04-02 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (4, 1, 'credit', 1, 1000, 1);

            -- 5) 第 17 期の期末（03-30）に取引し、その期のうちに計上した。**id はいちばん大きい。**
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('第 17 期のうちに計上した取引', (SELECT id FROM fiscal_years WHERE code = 'FY17'),
                        '2026-03-30', '2026-03-30', 'draft', 'normal', '2026-03-30 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (5, 1, 'debit', 1, 700, 1);

            -- 6) 同じ 03-30 の取引を、決算後に見つけて第 18 期で計上した。
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('決算後に見つけて第 18 期で計上した取引', 1, '2026-03-30', '2026-04-05', 'draft', 'normal', '2026-04-05 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (6, 1, 'debit', 1, 800, 1);

            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2027-04-02 10:00:00' WHERE id = 4;
            UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-03-30 10:00:00' WHERE id = 5;
            UPDATE journal_entries SET status = 'posted', entry_no = 3, posted_at = '2026-04-05 10:00:00' WHERE id = 6;
            """);

        var rows = Run(db);

        // 取引日が同じ 03-30 の 2 行。**第 17 期（id が最大）が先、第 18 期（id が 1）が後。**
        // 識別子で並べると逆になる。
        Assert.Equal([5L, 6L], rows.Where(r => r.EntryId is 5 or 6).Select(r => r.EntryId));

        // 取引日が同じ 05-10 の 3 行。第 18 期の #1 が先、第 19 期の #1 が後。
        Assert.Equal([1L, 1L, 4L], rows.Where(r => r.EntryId is 1 or 4).Select(r => r.EntryId));

        // 帳簿全体では 03-30 の 2 行がいちばん前に来る（取引日が先頭のキー）。
        Assert.Equal([5L, 6L, 1L, 1L, 4L, 2L, 2L], rows.Select(r => r.EntryId));
    }

    /// <summary>
    /// <b>取引年月日に時刻が付いていても、日付として並ぶ。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>格納形は一様ではない。</b> DDL が受理するのは 5 書式で（<c>Designer/ddl/011</c>）、
    /// CLB は同じ日を <c>'2026-05-22'</c> とも <c>'2026-05-22 00:00:00'</c> とも書く（qa/01 の A-04）。
    /// <c>ORDER BY</c> の <c>date()</c> を剥がすと<b>時刻の付いた行だけが同じ日の最後へ回る</b>
    /// ——例外は出ず、帳簿の並びだけが静かに狂う（qa/03 の L-12 と同じ型）。</para>
    /// <para><b>この穴は、行セットの差分で殺す掃引が見つけた</b>（ADR-0058）——
    /// 取引年月日の <c>date()</c> を剥がしても、<b>どの入力でも 1 行も変わらなかった</b>。
    /// 検体の取引年月日が裸の日付しか持たず、<b>同じ日に 2 通りの書き方が並ぶ形が無かった</b>からである。</para>
    /// </remarks>
    [Fact]
    public void 取引年月日に時刻が付いていても日付として並ぶ()
    {
        using var db = Create();
        TestDatabase.Execute(db, """
            -- 4) 05-22 の取引。**時刻つきで格納する。** 伝票番号は 3。
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('時刻つきで記録した取引', 1, '2026-05-22 00:00:00', '2026-05-22', 'draft', 'normal', '2026-05-22 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (4, 1, 'debit', 1, 600, 1);

            -- 5) 同じ 05-22 の取引を、**日付だけ**で格納する。伝票番号は 4。
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('日付だけで記録した取引', 1, '2026-05-22', '2026-05-22', 'draft', 'normal', '2026-05-22 11:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (5, 1, 'debit', 1, 700, 1);

            UPDATE journal_entries SET status = 'posted', entry_no = 3, posted_at = '2026-05-22 10:00:00' WHERE id = 4;
            UPDATE journal_entries SET status = 'posted', entry_no = 4, posted_at = '2026-05-22 11:00:00' WHERE id = 5;
            """);

        // **この検体が計器として効く前提を、先に表明する。**
        // **時刻つきの伝票（id 4）のほうが伝票番号が小さい**——逆に振ると、
        // `date()` を剥がしても並びが変わらず、**テストは緑のまま計器としての意味を失う**。
        Assert.Equal(3L, EntryNoOf(db, "2026-05-22 00:00:00"));
        Assert.Equal(4L, EntryNoOf(db, "2026-05-22"));

        // 05-22 の 2 行は**伝票番号の順**（3 → 4 ＝ id 4 → id 5）に並ぶ。
        // 文字列として比べると、時刻の付いた 3 番が同じ日の最後へ回って id 5 → id 4 になる。
        Assert.Equal("1-1 1-2 2-1 2-2 4-1 5-1", Joined(Run(db)));
    }

    /// <summary>格納された取引年月日の字面で伝票を引き、その伝票番号を返す。</summary>
    private static long EntryNoOf(SqliteConnection db, string transactionDate)
        => TestDatabase.ScalarOf<long>(
            db, $"SELECT entry_no FROM journal_entries WHERE transaction_date = '{transactionDate}'");

    /// <summary>
    /// <b>会計年度を列に出す。</b> 伝票番号は年度ごとの連番なので、年度で絞らなければ
    /// 同じ番号が何行も並ぶ。帳簿は既定で絞らない（docs/21 §3）ので、既定の表示がその状態である。
    /// </summary>
    [Fact]
    public void 会計年度は列に出る()
    {
        using var db = Create();

        Assert.All(Run(db), r => Assert.Equal("第 18 期（2026 年度）", r.FiscalYearLabel));
    }

    // --- 実行の土台 ---

    /// <param name="beforePosting">
    /// 計上する前に流す SQL。計上済みは変更できないので、行の差異はここで入れる。
    /// </param>
    private static SqliteConnection Create(string beforePosting = "")
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, Book);
        if (beforePosting.Length > 0)
        {
            TestDatabase.Execute(db, beforePosting);
        }

        // **摘要のない計上済みは、いまは作れない**（docs/10 §4-2-1）。
        // だが**規則より前に計上された行が稼働 DB に 2 件あり、計上済みは直せない**（I-05）。
        // 帳簿はその行も探せなければならない（電帳通達 8-13。「記録事項がない行を探せる」）ので、
        // **その 1 本だけ**トリガを外して作る。**外した定義は sqlite_master から読んで貼り直す。**
        const string PostingGuard = "trg_journal_entries_description_required_when_posted";
        TestDatabase.Execute(db, Post);
        TestDatabase.WithoutTrigger(db, PostingGuard, PostBlankDescription);

        // **摘要が空の計上済みは 2 番だけ**。検体に伝票を足した人が摘要を書き忘れたら、ここで鳴る。
        // **2 番を除いて数える**——テストによっては 2 番の摘要を計上の前に埋めるので、
        // 「空が 1 本ある」ではなく「2 番のほかに空が無い」を表明する。
        Assert.Equal(0L, TestDatabase.ScalarOf<long>(
            db,
            """
            SELECT COUNT(*) FROM journal_entries
             WHERE status = 'posted' AND id <> 2 AND (description IS NULL OR trim(description) = '')
            """));
        return db;
    }

    private sealed record Row(long EntryId, string FiscalYearLabel, long LineNo, string? PartnerName);

    /// <summary>
    /// 返った行を <c>&lt;伝票&gt;-&lt;行&gt;</c> の並びにする。
    /// </summary>
    /// <remarks>
    /// <b>件数ではなく、どの行が返ったかを表明するため</b>にある（qa/03 の L-46）。
    /// <b>並びも一緒に固定される</b>ので、<c>ORDER BY</c> の取り違えもここで落ちる。
    /// </remarks>
    private static string Joined(IEnumerable<Row> rows)
        => string.Join(" ", rows.Select(row => $"{row.EntryId}-{row.LineNo}"));

    /// <summary>
    /// 仕訳帳の SQL を<b>本物のまま</b>流す。渡さなかったパラメータは NULL（＝条件なし）。
    /// </summary>
    private static IReadOnlyList<Row> Run(SqliteConnection db, params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.CommandText = TestDatabase.QuerySql(Module);

        foreach (var name in Parameters)
        {
            var (givenName, givenValue) = parameters.FirstOrDefault(p => p.Name == name);
            command.Parameters.AddWithValue(name, givenName is null ? DBNull.Value : givenValue);
        }

        using var reader = SqlMutationProbe.ExecuteReader(command, Module);
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt64(reader.GetOrdinal("entry_id")),
                reader.GetString(reader.GetOrdinal("fiscal_year_label")),
                reader.GetInt64(reader.GetOrdinal("line_no")),
                reader.IsDBNull(reader.GetOrdinal("partner_name"))
                    ? null : reader.GetString(reader.GetOrdinal("partner_name"))));
        }

        return rows;
    }

    /// <summary>
    /// SQL が使う入力パラメータ。<b>足りないと SQLite が実行時に落ちる</b>ので、
    /// 一覧が古くなったことはテストの失敗として現れる。
    /// </summary>
    private static readonly string[] Parameters =
    [
        "@p_fiscal_year_id", "@p_transaction_date_from", "@p_transaction_date_to",
        "@p_amount_min", "@p_amount_max", "@p_entry_no_min", "@p_entry_no_max",
        "@p_account_id", "@p_partner_id", "@p_keyword", "@p_blank_field",
    ];
}
