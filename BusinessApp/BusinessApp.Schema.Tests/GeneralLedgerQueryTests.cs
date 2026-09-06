namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;

/// <summary>
/// 総勘定元帳が<b>帳簿として正しく出ること</b>（ADR-0022）。
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は宣言と SQL の整合しか見ない。
/// 元帳が持つ計算——<b>相手勘定科目</b>（法税規則 55 ②）と<b>期間内累計</b>——は、
/// 値を入れて数えないと合っているか分からない。どちらも間違えても例外にならず、
/// <b>それらしい数字が静かに出る</b>（qa/03 L-15 の型）。</para>
/// <para>とくに累計の符号は、科目区分だけで決めると評価勘定で必ず誤る（docs/10 §6）。
/// 4 つの組み合わせ（科目区分が借方側か × 評価勘定か）をすべて通す。</para>
/// </remarks>
public class GeneralLedgerQueryTests
{
    /// <summary>
    /// 元帳に載る素材。<b>通常残高の 4 通りを揃える</b>——
    /// 資産（現金）・資産の評価勘定（減価償却累計額）・収益（売上高）・収益の評価勘定（売上値引高）。
    /// </summary>
    private const string Accounts = """
        INSERT INTO accounts (code, name, category, is_contra) VALUES ('1500', '減価償却累計額', 'asset', 1);
        INSERT INTO accounts (code, name, category) VALUES ('2000', '買掛金', 'liability');
        INSERT INTO accounts (code, name, category, is_contra) VALUES ('4900', '売上値引高', 'revenue', 1);
        INSERT INTO accounts (code, name, category) VALUES ('5300', '減価償却費', 'expense');
        """;

    /// <summary>
    /// 仕訳。<b>1 対 1 の伝票と、相手が 2 科目に割れる伝票の両方</b>を入れる（諸口の検査）。
    /// </summary>
    private const string Book = """
        -- 1) 05-10 借 現金 1,000 / 貸 売上高 1,000（相手は 1 科目）
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, partner_id, entered_at)
            VALUES (1, '2026-05-10', '2026-05-12', 'draft', 'normal', '5 月の売上', 1, '2026-05-12 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id, item_description)
            VALUES (1, 1, 'debit', 1, 1000, 1, '商品 A');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
            VALUES (1, 2, 'credit', 2, 2, 1000, 1);

        -- 2) 05-20 借 現金 3,000 / 貸 売上高 2,000 ＋ 買掛金 1,000（現金から見た相手は 2 科目＝諸口）
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-20', '2026-05-25', 'draft', 'normal', '2026-05-25 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 1, 'debit', 1, 3000, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 2, 'credit', 2, 2000, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (2, 3, 'credit', 4, 1000, 1);

        -- 3) 05-25 借 減価償却費 500 / 貸 減価償却累計額 500（評価勘定・貸方が通常残高）
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-25', '2026-05-25', 'draft', 'normal', '2026-05-25 11:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 1, 'debit', 6, 500, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 2, 'credit', 3, 500, 1);

        -- 4) 05-28 借 売上値引高 300 / 貸 現金 300（評価勘定・借方が通常残高）
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-28', '2026-05-28', 'draft', 'normal', '2026-05-28 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (4, 1, 'debit', 5, 300, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (4, 2, 'credit', 1, 300, 1);

        -- 5) 下書き。**帳簿には出ない。**
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-15', '2026-05-15', 'draft', 'normal', '2026-05-15 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (5, 1, 'debit', 1, 9999, 1);
        """;

    private const string Post = """
        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-12 10:00:00' WHERE id = 1;
        UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-25 10:00:00' WHERE id = 2;
        UPDATE journal_entries SET status = 'posted', entry_no = 3, posted_at = '2026-05-25 11:00:00' WHERE id = 3;
        UPDATE journal_entries SET status = 'posted', entry_no = 4, posted_at = '2026-05-28 10:00:00' WHERE id = 4;
        """;

    // --- 出す行の範囲と並び ---

    [Fact]
    public void 下書きは元帳に出ない()
    {
        using var db = Create();

        // 下書きの 9,999 円はどこにも無い。
        Assert.DoesNotContain(Run(db), r => r.Debit == 9999 || r.Credit == 9999);
        Assert.Equal(9, Run(db).Count);
    }

    /// <summary>
    /// <b>科目コード順に並び、その中は取引日順。</b> これが元帳の形そのものである。
    /// </summary>
    [Fact]
    public void 科目ごとにまとまり科目の中は取引日順に並ぶ()
    {
        using var db = Create();

        Assert.Equal(
            ["1100", "1100", "1100", "1500", "2000", "4000", "4000", "4900", "5300"],
            Run(db).Select(r => r.AccountCode));
    }

    // --- 相手勘定科目（法税規則 55 ②）---

    [Fact]
    public void 相手が一科目ならその名前が出る()
    {
        using var db = Create();

        var row = Run(db).Single(r => r.AccountCode == "1100" && r.EntryNo == 1);
        Assert.Equal("売上高", row.CounterAccountName);
    }

    /// <summary>
    /// <b>相手が 2 科目以上なら「諸口」。</b> ここを 1 件目の名前で埋めると、
    /// 帳簿が「現金 3,000 の相手は売上高」と嘘をつく（実際には売上高 2,000 と買掛金 1,000）。
    /// </summary>
    [Fact]
    public void 相手が複数なら諸口になる()
    {
        using var db = Create();

        var row = Run(db).Single(r => r.AccountCode == "1100" && r.EntryNo == 2);
        Assert.Equal("諸口", row.CounterAccountName);
    }

    /// <summary>
    /// 同じ伝票でも、**見る側が変われば相手も変わる**。
    /// 売上高から見た相手は現金 1 科目なので諸口にならない。
    /// </summary>
    [Fact]
    public void 相手は行ごとに決まる()
    {
        using var db = Create();

        Assert.Equal("現金", Run(db).Single(r => r.AccountCode == "4000" && r.EntryNo == 2).CounterAccountName);
        Assert.Equal("現金", Run(db).Single(r => r.AccountCode == "2000" && r.EntryNo == 2).CounterAccountName);
    }

    // --- 借方と貸方の振り分け ---

    [Fact]
    public void 借方と貸方は別の列に出て片方は空になる()
    {
        using var db = Create();
        var rows = Run(db);

        var debit = rows.Single(r => r.AccountCode == "1100" && r.EntryNo == 1);
        Assert.Equal(1000, debit.Debit);
        Assert.Null(debit.Credit);

        var credit = rows.Single(r => r.AccountCode == "4000" && r.EntryNo == 1);
        Assert.Null(credit.Debit);
        Assert.Equal(1000, credit.Credit);
    }

    // --- 期間内累計（docs/10 §6 の通常残高）---

    /// <summary>
    /// <b>累計は科目ごとに積み上がる。</b> 科目で区切らずに積むと、
    /// 別の科目の金額が混ざった数字が「その科目の累計」として出る。
    /// </summary>
    [Fact]
    public void 累計は科目ごとに積み上がる()
    {
        using var db = Create();
        var cash = Run(db).Where(r => r.AccountCode == "1100").ToList();

        // 借方 1,000 → 借方 3,000 → 貸方 300。現金は借方が通常残高。
        Assert.Equal([1000, 4000, 3700], cash.Select(r => r.RunningTotal));
    }

    /// <summary>
    /// 収益は<b>貸方が通常残高</b>。借方 − 貸方で積むと売上の累計が負の数で出る。
    /// </summary>
    [Fact]
    public void 収益の累計は貸方を正にする()
    {
        using var db = Create();

        Assert.Equal([1000, 3000], Run(db).Where(r => r.AccountCode == "4000").Select(r => r.RunningTotal));
    }

    /// <summary>
    /// <b>評価勘定は科目区分と逆。</b> 減価償却累計額（資産の評価勘定）は貸方が、
    /// 売上値引高（収益の評価勘定）は借方が通常残高になる。
    /// 科目区分だけで符号を決める実装だと、この 2 件だけが逆符号で落ちる。
    /// </summary>
    [Theory]
    [InlineData("1500", 500)]    // 資産の評価勘定 ＝ 貸方が通常残高
    [InlineData("4900", 300)]    // 収益の評価勘定 ＝ 借方が通常残高
    [InlineData("2000", 1000)]   // 負債 ＝ 貸方が通常残高
    [InlineData("5300", 500)]    // 費用 ＝ 借方が通常残高
    public void 通常残高の側を正にする(string accountCode, long expected)
    {
        using var db = Create();

        Assert.Equal(expected, Run(db).Single(r => r.AccountCode == accountCode).RunningTotal);
    }

    /// <summary>
    /// <b>損益科目の累計は、会計年度をまたいで積み上がらない</b>（開発者の決定。2026-08-28）。
    /// </summary>
    /// <remarks>
    /// 収益と費用は決算で振り替えられて残高が消えるので、年度をまたいで積むと意味のない数になる。
    /// <b>会計年度が 1 つのうちは表に出ない</b>——第 17 期のデモデータか翌期が入って初めて現れる
    /// ので、年度を複数持つ検体でしか捕まえられない。
    /// </remarks>
    [Fact]
    public void 損益科目の累計は会計年度ごとに積み直す()
    {
        using var db = CreateWithOtherYears();

        // 売上高（収益）。第 17 期 50 → 第 18 期 1,000・3,000 → 第 19 期 **100 から積み直す**。
        Assert.Equal(
            [50, 1000, 3000, 100, 800],
            Run(db).Where(r => r.AccountCode == "4000").Select(r => r.RunningTotal));
    }

    /// <summary>
    /// <b>貸借科目の累計は切らない。</b> 現金の残高は年度をまたいで続くものであり、
    /// 年度で 0 に戻すと手許現金でも残高でもない数が出る（docs/10 の I-07）。
    /// </summary>
    /// <remarks>
    /// 開発者の決定の理由は「<b>損益科目</b>の累計が決算をまたいで積み上がる」だった。
    /// 全科目を一律に切ると、その理由が当たらない科目まで巻き添えにする。
    /// </remarks>
    [Fact]
    public void 貸借科目の累計は会計年度をまたいで積み上がる()
    {
        using var db = CreateWithOtherYears();
        var cash = Run(db).Where(r => r.AccountCode == "1100").ToList();

        Assert.Equal([50, 1050, 4050, 3750, 3850, 4550], cash.Select(r => r.RunningTotal));

        // 最後の値は、この検体の現金の出入りをすべて足した額そのものである。
        Assert.Equal(50 + 1000 + 3000 - 300 + 100 + 700, cash[^1].RunningTotal);
    }

    /// <summary>
    /// <b>並びは会計年度の開始日で決める。</b> <c>fiscal_years.id</c> は代理キーなので、
    /// 識別子で並べると<b>後から入れた第 17 期が第 19 期より後ろに出る</b>。
    /// </summary>
    /// <remarks>
    /// あわせて、検体の 7 番は<b>取引日が第 18 期・会計年度が第 19 期</b>の伝票である
    /// （決算後に見つかった取引）。取引日を年度より先に並べると、この行が第 18 期の行の間に挟まり、
    /// 損益科目ではそこだけ累計が別の系列の値を出す。
    /// 会計期間への帰属を決めるのは計上日である（docs/10 の I-03）。
    /// </remarks>
    [Fact]
    public void 科目の中は会計年度の古い順にまとまる()
    {
        using var db = CreateWithOtherYears();
        var cash = Run(db).Where(r => r.AccountCode == "1100").ToList();

        Assert.Equal(
            ["第 17 期", "第 18 期（2026 年度）", "第 18 期（2026 年度）", "第 18 期（2026 年度）",
             "第 19 期", "第 19 期"],
            cash.Select(r => r.FiscalYearLabel));

        // 7 番（第 19 期・取引日 2026-05-15）は取引日で並べると 1 番と 2 番の間に来るが、
        // 年度でまとまるので第 19 期のまとまりの先頭に入る。
        Assert.Equal([1, 1, 2, 4, 2, 1], cash.Select(r => r.EntryNo));
    }

    /// <summary>
    /// <b>年度を列に出す。</b> 出さないと、累計が突然戻る理由も、取引日が遡る理由も画面に現れない。
    /// 帳簿は既定で絞らない（docs/21 §3）ので、既定の表示は必ず年度が混ざる。
    /// </summary>
    [Fact]
    public void 会計年度は列に出る()
    {
        using var db = Create();

        Assert.All(Run(db), r => Assert.Equal("第 18 期（2026 年度）", r.FiscalYearLabel));
    }

    // --- 絞り込み ---

    [Fact]
    public void 勘定科目で絞ると一科目だけになる()
    {
        using var db = Create();

        var rows = Run(db, ("@p_account_id", 1L));
        Assert.All(rows, r => Assert.Equal("1100", r.AccountCode));
        Assert.Equal(3, rows.Count);
    }

    /// <summary>
    /// <b>絞り込んだ範囲の中で積み直す。</b> 期間内累計は「いま出ている行の累計」であり、
    /// 絞る前の値を引きずらない。
    /// </summary>
    [Fact]
    public void 絞り込むと累計も絞り込んだ範囲で積み直される()
    {
        using var db = Create();

        // 05-20 以降だけを見ると、現金は 3,000 → 2,700 になる（1,000 は範囲外）。
        var rows = Run(db, ("@p_account_id", 1L), ("@p_transaction_date_from", "2026-05-20"));
        Assert.Equal([3000, 2700], rows.Select(r => r.RunningTotal));
    }

    [Theory]
    [InlineData(1000, 3000, 5)]
    [InlineData(1001, 3000, 2)]
    [InlineData(3001, 9999, 0)]
    public void 金額の範囲は両端を含む(long min, long max, int expected)
    {
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_amount_min", min), ("@p_amount_max", max)).Count);
    }

    [Fact]
    public void 伝票番号で絞れる()
    {
        using var db = Create();

        Assert.All(Run(db, ("@p_entry_no_min", 3L), ("@p_entry_no_max", 3L)), r => Assert.Equal(3, r.EntryNo));
    }

    [Fact]
    public void 取引先で絞れる()
    {
        using var db = Create();

        // 取引先は 1 番の伝票にだけ付いている（2 行）。
        var rows = Run(db, ("@p_partner_id", 1L));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(1, r.EntryNo));
    }

    // --- 取引先別の帳簿（ADR-0042。総勘定元帳を取引先で絞って売掛帳・買掛帳にする前提）---

    /// <summary>
    /// <b>取引先で絞ると、累計もその取引先の行だけで積み上がる。</b>
    /// 別表二十四（四）（五）の「相手方別に」を、総勘定元帳を取引先で絞ることで満たす前提
    /// （ADR-0042・docs/40 §4-1）は、これが成り立って初めて言える。
    /// </summary>
    /// <remarks>
    /// <para>窓関数は <c>WHERE</c> の後に評価されるので、絞れば取引先ごとの累計になる——
    /// <b>読みで「済」と書かず、数えて固定する</b>（qa/02 ラウンド 43）。</para>
    /// <para>検体は 3 つの形を通す——<b>取引先が明細側にだけ付いた伝票</b>（伝票側は空。
    /// <c>COALESCE(l.partner_id, e.partner_id)</c> の明細側）、<b>取引先が伝票側に付いた伝票</b>、
    /// <b>その取消</b>（反対仕訳は原仕訳と同じ取引日・同じ取引先。docs/10 §5）。</para>
    /// </remarks>
    [Fact]
    public void 取引先で絞ると累計はその取引先の行だけで積み上がる()
    {
        using var db = CreateWithPartnerLedger();

        // 取引先 1 の現金: 1 番 借 1,000 → 6 番 借 600 → 7 番（6 番の取消）貸 600。
        var first = Run(db, ("@p_account_id", 1L), ("@p_partner_id", 1L));
        Assert.Equal([1, 6, 7], first.Select(r => r.EntryNo));
        Assert.Equal([1000, 1600, 1000], first.Select(r => r.RunningTotal));

        // 取引先 2 の現金: 5 番 借 400 だけ。取引先 1 の 1,000 を引きずらない。
        var second = Run(db, ("@p_account_id", 1L), ("@p_partner_id", 2L));
        Assert.Equal([5], second.Select(r => r.EntryNo));
        Assert.Equal([400], second.Select(r => r.RunningTotal));

        // 絞らなければ、同じ現金の累計は全取引先が混ざった 1 本の系列になる。
        Assert.Equal(
            [1000, 4000, 3700, 4100, 4700, 4100],
            Run(db, ("@p_account_id", 1L)).Select(r => r.RunningTotal));
    }

    [Fact]
    public void 部門で絞れる()
    {
        using var db = Create();

        var rows = Run(db, ("@p_department_id", 2L));
        Assert.Equal(["4000"], rows.Select(r => r.AccountCode));
    }

    [Fact]
    public void 摘要でも内容でも引ける()
    {
        using var db = Create();

        Assert.Equal(2, Run(db, ("@p_keyword", "5 月の売上")).Count);   // 摘要（伝票）
        Assert.Single(Run(db, ("@p_keyword", "商品 A")));               // 内容（明細）
    }

    [Fact]
    public void 打った文字はワイルドカードにならない()
    {
        using var db = Create();

        Assert.Empty(Run(db, ("@p_keyword", "%")));
    }

    [Theory]
    [InlineData("partner", 7)]           // 取引先が付いているのは 1 番の 2 行だけ
    [InlineData("department", 8)]        // 部門が付いているのは 1 行だけ
    [InlineData("sub_account", 9)]       // 補助科目はどこにも無い
    [InlineData("description", 7)]       // 摘要があるのは 1 番だけ
    [InlineData("item_description", 8)]  // 内容があるのは 1 行だけ
    public void 記録事項がない行を探せる(string field, int expected)
    {
        using var db = Create();

        Assert.Equal(expected, Run(db, ("@p_blank_field", field)).Count);
    }

    [Fact]
    public void 知らない空値の指定は一件も返さない()
    {
        using var db = Create();

        Assert.Empty(Run(db, ("@p_blank_field", "unknown")));
    }

    [Fact]
    public void 別の会計年度を指定すると一件も出ない()
    {
        using var db = Create();

        Assert.Empty(Run(db, ("@p_fiscal_year_id", 99L)));
    }

    // --- 実行の土台 ---

    private static SqliteConnection Create()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, Accounts);
        TestDatabase.Execute(db, Book);
        TestDatabase.Execute(db, Post);
        return db;
    }

    /// <summary>
    /// 第 17 期と第 19 期を足した検体。<b>会計年度をまたぐ話はこれでしか検査できない。</b>
    /// </summary>
    /// <remarks>
    /// 伝票を 3 本足す——6 番は素直に第 19 期の取引、
    /// <b>7 番は取引日が第 18 期・計上日が第 19 期</b>（決算後に見つかった取引）、
    /// <b>8 番はいちばん古い第 17 期なのに識別子はいちばん大きい</b>。
    /// 7 番が無いと取引日順と年度順が食い違う形を、
    /// 8 番が無いと識別子順と年代順が食い違う形を、一度も通さないことになる。
    /// </remarks>
    private static SqliteConnection CreateWithOtherYears()
    {
        var db = Create();
        TestDatabase.Execute(db, OtherYears);
        return db;
    }

    /// <remarks>
    /// <para><b>第 19 期を先に、第 17 期を後から入れる。</b> <c>fiscal_years.id</c> は
    /// <c>AUTOINCREMENT</c> の代理キーなので、こうすると<b>識別子の順と年代の順が食い違う</b>——
    /// 第 17 期は id が最大なのに、いちばん古い。並びを id で決めていると、この検体で必ず落ちる。
    /// 逆に、後の年度ほど id が大きい検体しか持たないと、<b>誤った実装でも緑になる</b>（qa/03 L-02）。</para>
    /// <para>年度は<b>コードから引く</b>（初期データの id を書き写さない）。</para>
    /// </remarks>
    private const string OtherYears = """
        INSERT INTO fiscal_years (code, label, start_date, end_date, status)
            VALUES ('FY19', '第 19 期', '2027-04-01', '2028-03-31', 'open');
        INSERT INTO fiscal_years (code, label, start_date, end_date, status)
            VALUES ('FY17', '第 17 期', '2025-04-01', '2026-03-31', 'open');

        -- 6) 第 19 期 2027-04-10 借 現金 700 / 貸 売上高 700
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ((SELECT id FROM fiscal_years WHERE code = 'FY19'),
                    '2027-04-10', '2027-04-10', 'draft', 'normal', '2027-04-10 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (6, 1, 'debit', 1, 700, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (6, 2, 'credit', 2, 700, 1);

        -- 7) **取引日は第 18 期（2026-05-15）・計上日は第 19 期。** 決算後に見つかった取引。
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ((SELECT id FROM fiscal_years WHERE code = 'FY19'),
                    '2026-05-15', '2027-04-20', 'draft', 'normal', '2027-04-20 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 1, 'debit', 1, 100, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 2, 'credit', 2, 100, 1);

        -- 8) 第 17 期 2025-05-10 借 現金 50 / 貸 売上高 50。**いちばん古いのに id はいちばん大きい。**
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ((SELECT id FROM fiscal_years WHERE code = 'FY17'),
                    '2025-05-10', '2025-05-10', 'draft', 'normal', '2025-05-10 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 1, 'debit', 1, 50, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 2, 'credit', 2, 50, 1);

        -- 伝票番号は会計年度の中の連番（I-17）。年度が変われば 1 番から採り直す。
        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2027-04-10 10:00:00' WHERE id = 6;
        UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2027-04-20 10:00:00' WHERE id = 7;
        UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2025-05-10 10:00:00' WHERE id = 8;
        """;

    /// <summary>
    /// 取引先別の累計を数える検体。<b>取引先の付け方を 2 通り（明細側・伝票側）と、取消を 1 本</b>入れる。
    /// </summary>
    private static SqliteConnection CreateWithPartnerLedger()
    {
        var db = Create();
        TestDatabase.Execute(db, PartnerLedger);
        return db;
    }

    /// <remarks>
    /// 伝票の識別子は基本の検体の続き（6〜8）。伝票番号は第 18 期の連番の続き（5〜7）。
    /// </remarks>
    private const string PartnerLedger = """
        INSERT INTO partners (code, name) VALUES ('P002', '乙商事');

        -- 6) 05-30 借 現金 400 / 貸 売上高 400。**取引先 2 は明細側にだけ付ける**（伝票側は空）。
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES (1, '2026-05-30', '2026-05-30', 'draft', 'normal', '2026-05-30 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, partner_id, amount, tax_category_id)
            VALUES (6, 1, 'debit', 1, 2, 400, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (6, 2, 'credit', 2, 400, 1);

        -- 7) 05-31 借 現金 600 / 貸 売上高 600。**取引先 1 は伝票側**。
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, partner_id, entered_at)
            VALUES (1, '2026-05-31', '2026-05-31', 'draft', 'normal', 1, '2026-05-31 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 1, 'debit', 1, 600, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 2, 'credit', 2, 600, 1);

        -- 8) 7 番の取消。反対仕訳は原仕訳と同じ取引日・同じ取引先で、貸借を入れ替える（docs/10 §5）。
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, partner_id, entered_at)
            VALUES (1, '2026-05-31', '2026-06-01', 'draft', 'reversal', 7, 1, '2026-06-01 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 1, 'debit', 2, 600, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 2, 'credit', 1, 600, 1);

        UPDATE journal_entries SET status = 'posted', entry_no = 5, posted_at = '2026-05-30 10:00:00' WHERE id = 6;
        UPDATE journal_entries SET status = 'posted', entry_no = 6, posted_at = '2026-05-31 10:00:00' WHERE id = 7;
        UPDATE journal_entries SET status = 'posted', entry_no = 7, posted_at = '2026-06-01 10:00:00' WHERE id = 8;
        """;

    private sealed record Row(
        string AccountCode, string FiscalYearLabel, int EntryNo, string? CounterAccountName,
        long? Debit, long? Credit, long RunningTotal);

    /// <summary>
    /// 元帳の SQL を<b>本物のまま</b>流す。渡さなかったパラメータは NULL（＝条件なし）。
    /// </summary>
    private static IReadOnlyList<Row> Run(SqliteConnection db, params (string Name, object Value)[] parameters)
    {
        using var command = db.CreateCommand();
        command.CommandText = File.ReadAllText(TestDatabase.QuerySqlOf("GeneralLedger"));

        foreach (var name in Parameters)
        {
            var (givenName, givenValue) = parameters.FirstOrDefault(p => p.Name == name);
            command.Parameters.AddWithValue(name, givenName is null ? DBNull.Value : givenValue);
        }

        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetString(reader.GetOrdinal("account_code")),
                reader.GetString(reader.GetOrdinal("fiscal_year_label")),
                reader.GetInt32(reader.GetOrdinal("entry_no")),
                Nullable(reader, "counter_account_name") is int counter ? reader.GetString(counter) : null,
                Nullable(reader, "debit_amount") is int debit ? reader.GetInt64(debit) : null,
                Nullable(reader, "credit_amount") is int credit ? reader.GetInt64(credit) : null,
                reader.GetInt64(reader.GetOrdinal("running_total"))));
        }

        return rows;
    }

    /// <summary>値が入っている列の位置。NULL なら <c>null</c>。</summary>
    private static int? Nullable(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : ordinal;
    }

    /// <summary>
    /// SQL が使う入力パラメータ。<b>足りないと SQLite が実行時に落ちる</b>ので、
    /// 一覧が古くなったことはテストの失敗として現れる。
    /// </summary>
    private static readonly string[] Parameters =
    [
        "@p_fiscal_year_id", "@p_account_id", "@p_sub_account_id", "@p_department_id", "@p_partner_id",
        "@p_transaction_date_from", "@p_transaction_date_to",
        "@p_amount_min", "@p_amount_max", "@p_entry_no_min", "@p_entry_no_max",
        "@p_keyword", "@p_blank_field",
    ];
}
