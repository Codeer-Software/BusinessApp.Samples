namespace BusinessApp.Schema.Tests;

using System.Globalization;

using BusinessApp.TestSupport;
using Microsoft.Data.Sqlite;

/// <summary>
/// 総勘定元帳が<b>帳簿として正しく出ること</b>（ADR-0022）。
/// </summary>
/// <remarks>
/// <para><see cref="QueryModuleTests"/> は宣言と SQL の整合しか見ない。
/// 元帳が持つ計算——<b>相手勘定科目</b>（法税規則 55 ②）と<b>期間内累計</b>——は、
/// 値を入れて数えないと合っているか分からない。どちらも間違えても例外にならず、
/// <b>それらしい数字が静かに出る</b>（qa/03 の L-12・L-19・L-20）。</para>
/// <para>とくに累計の符号は、科目区分だけで決めると評価勘定で必ず誤る（docs/15 §1）。
/// 4 つの組み合わせ（科目区分が借方側か × 評価勘定か）をすべて通す。</para>
/// </remarks>
[Collection(QuerySqlCollection.Name)]
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
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            VALUES (1, '2026-05-25', '2026-05-25', 'draft', 'normal', '5 月の減価償却', '2026-05-25 11:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 1, 'debit', 6, 500, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (3, 2, 'credit', 3, 500, 1);

        -- 4) 05-28 借 売上値引高 300 / 貸 現金 300（評価勘定・借方が通常残高）
        INSERT INTO journal_entries (fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
            VALUES (1, '2026-05-28', '2026-05-28', 'draft', 'normal', '値引の計上', '2026-05-28 10:00:00');
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
        UPDATE journal_entries SET status = 'posted', entry_no = 3, posted_at = '2026-05-25 11:00:00' WHERE id = 3;
        UPDATE journal_entries SET status = 'posted', entry_no = 4, posted_at = '2026-05-28 10:00:00' WHERE id = 4;
        """;

    /// <summary>**摘要のない**伝票 2 の計上。<b>いまの製品では作れない形</b>なので、トリガを外して作る。</summary>
    private const string PostBlankDescription =
        "UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-25 10:00:00' WHERE id = 2";

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

    // --- 期間内累計（docs/15 §1 の通常残高）---

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

        // **減価償却費（費用）も同じく積み直す。** 第 18 期 500 → 第 19 期 **800 から**。
        // **これが無いと、`IN ('revenue', 'expense')` から 'expense' を落としても誰も赤くならない。**
        Assert.Equal(
            [500, 800],
            Run(db).Where(r => r.AccountCode == "5300").Select(r => r.RunningTotal));
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
    /// <b>docs/40 の F1'</b>（対象帳簿の全てを優良の要件で備え付ける）。
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
    [InlineData("description", 3)]       // 摘要が無いのは 2 番の 3 行だけ（本番では作れない形の再現）
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

    /// <summary>
    /// <b>16 列すべてを、行まるごと突き合わせる。</b>
    /// </summary>
    /// <remarks>
    /// <b>列の取り違えは、これでしか捕まらない。</b> <c>QueryModuleTests</c> が見るのは
    /// <b>別名だけ</b>である。<b>値が全部埋まった行と、任意の列が全部 NULL の行の 2 本</b>を見る
    /// ——埋まった行だけだと、NULL を読む経路が一度も通らない。
    /// </remarks>
    [Fact]
    public void 十六列がそれぞれの列の値を返す()
    {
        using var db = Create();

        var rows = Run(db);

        // 1 番の 1 行目: 部門も取引先も内容も摘要も入っている。
        Assert.Equal(
            new Row(1, 1, "1100", "現金", null, "第 18 期（2026 年度）", "2026-05-10", "normal",
                    "売上高", null, "株式会社取引先", 1000, null, 1000, "商品 A", "5 月の売上"),
            rows.Single(row => row.EntryNo == 1 && row.AccountCode == "1100"));

        // 2 番の 3 行目（買掛金）: 部門も取引先も内容も摘要も無い。
        Assert.Equal(
            new Row(2, 2, "2000", "買掛金", null, "第 18 期（2026 年度）", "2026-05-20", "normal",
                    "現金", null, null, null, 1000, 1000, null, null),
            rows.Single(row => row.AccountCode == "2000"));
    }

    // --- 2026-09-14 に足したぶん（SQL ミューテーションの掃引が報告した 18 点を撃つ。ADR-0056）---

    /// <summary>
    /// <b>相手勘定科目は、同じ科目が複数行あっても「諸口」にしない。</b>
    /// </summary>
    /// <remarks>
    /// <para>判定は <c>COUNT(DISTINCT o.account_id)</c> である。
    /// <b><c>DISTINCT</c> を落とすと、反対側に同じ科目が 2 行ある伝票で「諸口」に化ける</b>——
    /// <b>法税規則 55 ② の法定記載事項（相手勘定科目）が誤る</b>。</para>
    /// <para><b>いままでの検体では捕まらなかった。</b> 反対側が 2 行ある伝票はあったが、
    /// <b>どちらも別々の科目</b>だったので <c>COUNT</c> でも <c>COUNT(DISTINCT)</c> でも 2 になる。
    /// <b>同じ科目 2 行は DB が普通に作れる</b>（<c>UNIQUE (journal_entry_id, line_no)</c> は禁じていない）
    /// ——部門や取引先や税区分だけが違う明細である。</para>
    /// </remarks>
    [Fact]
    public void 相手勘定科目は同じ科目が二行あっても諸口にしない()
    {
        using var db = Create();
        PostExtra(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (90, 1, 'debit', 1, 8000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (90, 2, 'credit', 2, 5000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (90, 3, 'credit', 2, 2, 3000, 1);
            """);

        var borrowed = Run(db, ("@p_entry_no_min", 90L), ("@p_entry_no_max", 90L))
            .Single(row => row.Debit == 8000);

        Assert.Equal("売上高", borrowed.CounterAccountName);
    }

    /// <summary>金額の範囲は両端を含む。</summary>
    /// <remarks>
    /// <b>返った行を名指しで表明する</b>（qa/03 の L-46）。件数だけだと、
    /// <b>境界を外しても別の行が入れ替わりで入って件数が一致する</b>ことがある。
    /// </remarks>
    [Theory]
    [InlineData(1000L, 1000L, "1:-/1000 1:1000/- 2:-/1000")]
    [InlineData(1001L, null, "2:-/2000 2:3000/-")]
    [InlineData(null, 999L, "3:-/500 3:500/- 4:-/300 4:300/-")]
    [InlineData(500L, 500L, "3:-/500 3:500/-")]
    [InlineData(9999L, null, "")]
    public void 金額の範囲は両端を含む(long? min, long? max, string expected)
    {
        using var db = Create();

        Assert.Equal(expected, Lines(Run(db, ("@p_amount_min", min!), ("@p_amount_max", max!))));
    }

    /// <summary>伝票番号の範囲は両端を含む。</summary>
    [Theory]
    [InlineData(1L, 1L, "1:-/1000 1:1000/-")]
    [InlineData(2L, 3L, "2:-/1000 2:-/2000 2:3000/- 3:-/500 3:500/-")]
    [InlineData(4L, null, "4:-/300 4:300/-")]
    [InlineData(null, 1L, "1:-/1000 1:1000/-")]
    public void 伝票番号の範囲は両端を含み返る行まで決まる(long? min, long? max, string expected)
    {
        using var db = Create();

        Assert.Equal(expected, Lines(Run(db, ("@p_entry_no_min", min!), ("@p_entry_no_max", max!))));
    }

    /// <summary>
    /// <b>取引年月日の範囲は両端を含み、列にも検索値にも時刻が付いていて構わない。</b>
    /// </summary>
    /// <remarks>
    /// <b>2 つの向きが逆に効く。</b> 列に時刻が付いていると<b>終わりの境界</b>が落ち、
    /// 検索値に時刻が付いていると<b>始まりの境界</b>が落ちる（ADR-0055 の実測で証明した）。
    /// <b>4 通りの組み合わせを全部通す。</b>
    /// </remarks>
    [Theory]
    [InlineData("2026-05-26", "2026-05-26", "2026-05-26")]
    [InlineData("2026-05-26 00:00:00", "2026-05-26", "2026-05-26")]
    [InlineData("2026-05-26", "2026-05-26 00:00:00", "2026-05-26 00:00:00")]
    [InlineData("2026-05-26 00:00:00", "2026-05-26 00:00:00", "2026-05-26 00:00:00")]
    public void 取引年月日は時刻が付いていても日付として比べられる(string stored, string from, string to)
    {
        using var db = Create();
        PostExtra(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (90, 1, 'debit', 1, 8000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (90, 2, 'credit', 2, 8000, 1);
            """, transactionDate: stored);

        Assert.Equal(
            "90:-/8000 90:8000/-",
            Lines(Run(db, ("@p_transaction_date_from", from), ("@p_transaction_date_to", to))));
    }

    /// <summary>
    /// 検索欄が空のときは絞り込まない。<b>NULL と空文字の両方</b>で。
    /// </summary>
    /// <remarks>
    /// CLB は空の検索欄を <b>NULL または空文字</b>で束縛する（_specs/QueryAndSql.md）。
    /// <b>片方しか見ていないと、空欄のまま検索したときに 0 件になる</b>——
    /// 画面は「該当なし」を出すので、静かな失敗になる。<b>13 本のパラメータ全部に渡す。</b>
    /// </remarks>
    [Fact]
    public void 検索欄が空文字でも絞り込まない()
    {
        using var db = Create();

        var all = Lines(Run(db));

        Assert.NotEmpty(all);
        Assert.Equal(all, Lines(Run(db, [.. Parameters.Select(name => (name, (object)string.Empty))])));
    }

    /// <summary>
    /// <b>補助科目で絞れる。</b>
    /// </summary>
    /// <remarks>
    /// <b>この条件は入れた日から一度も撃たれていなかった</b>——検体に補助科目のある明細が無く、
    /// <c>l.sub_account_id = @p</c> の枝に 1 度も入っていない（2026-09-14 の掃引）。
    /// </remarks>
    [Fact]
    public void 補助科目で絞れる()
    {
        using var db = Create();
        // **補助科目を使う科目を新しく作る。** 既にある科目を「使う」に変えることはできない
        // ——**計上済みの明細が使っている科目の意味は変えられない**（ADR-0038。
        // `trg_accounts_meaning_frozen_when_posted` が拒む）。
        // **付ける先も「使う」科目でなければならない**
        // （`trg_journal_entries_sub_account_presence_when_posted` の 2 本目の RAISE）。
        TestDatabase.Execute(db, """
            INSERT INTO accounts (code, name, category, uses_sub_account) VALUES ('1200', '当座預金', 'asset', 1);
            INSERT INTO sub_accounts (account_id, code, name)
                SELECT id, 'S01', '甲銀行' FROM accounts WHERE code = '1200';
            INSERT INTO sub_accounts (account_id, code, name)
                SELECT id, 'S02', '乙銀行' FROM accounts WHERE code = '1200';
            """);
        PostExtra(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, sub_account_id, amount, tax_category_id)
                SELECT 90, 1, 'debit', a.id, s.id, 8000, 1
                  FROM accounts a JOIN sub_accounts s ON s.account_id = a.id
                 WHERE a.code = '1200' AND s.code = 'S01';
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (90, 2, 'credit', 2, 8000, 1);
            """);

        var first = TestDatabase.ScalarOf<long>(db, "SELECT id FROM sub_accounts WHERE code = 'S01'");
        var second = TestDatabase.ScalarOf<long>(db, "SELECT id FROM sub_accounts WHERE code = 'S02'");

        var picked = Run(db, ("@p_sub_account_id", first));

        Assert.Equal("90:8000/-", Lines(picked));
        // **絞り込めたことは、その値が画面に出ることを保証しない。**
        // 結合の先を取り違えていれば、絞り込みは効いても名前は空で返る。
        Assert.Equal("甲銀行", Assert.Single(picked).SubAccountName);
        Assert.Equal(string.Empty, Lines(Run(db, ("@p_sub_account_id", second))));
    }

    /// <summary>
    /// 計上済みの伝票を 1 本足す（伝票番号は 90 番）。
    /// </summary>
    /// <remarks>
    /// <b>既存の検体を動かさない。</b> `Book` を書き換えると、
    /// **いまある 25 本の期待値を全部読み直すことになる**。
    /// </remarks>
    private static void PostExtra(
        SqliteConnection db, string lines, string transactionDate = "2026-05-25", long id = 90)
    {
        TestDatabase.Execute(db, $"""
            INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, description, entered_at)
                VALUES ({id}, 1, '{transactionDate}', '2026-05-30', 'draft', 'normal', '足した伝票 {id}', '2026-05-30 10:00:00');
            {lines.Replace("{id}", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)}
            UPDATE journal_entries SET status = 'posted', entry_no = {id}, posted_at = '2026-05-30 11:00:00' WHERE id = {id};
            """);
    }

    /// <summary>
    /// <b>並びは日付として比べる。文字列としてではない。</b>
    /// </summary>
    /// <remarks>
    /// <para>DATE 列は <c>'YYYY-MM-DD'</c> でも <c>'YYYY-MM-DD 00:00:00'</c> でも入る
    /// （011 のトリガは 5 書式を受理する）。**本番で CLB が書くのは後者**（qa/01 の A-04）。
    /// <b>同じ日に両方の形が並ぶと、<c>date()</c> を外した瞬間に順序が入れ替わる</b>——
    /// 文字列としては <c>'2026-06-01'</c> のほうが短いぶん前に来るからである。</para>
    /// <para><b>検体が裸の日付だけだと、この違いは永久に見えない</b>——
    /// 掃引は <c>ORDER BY</c> と窓の <c>date(</c> を「生き残り」と報告し、
    /// **等価だと思い込むことになる**（2026-09-14 の自己レビュー）。</para>
    /// </remarks>
    [Fact]
    public void 帳簿の並びは日付として比べる()
    {
        using var db = Create();

        // 同じ取引日の 2 本。**時刻つきのほうが伝票番号は小さい。**
        PostExtra(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES ({id}, 1, 'debit', 1, 91, 1);
            """, transactionDate: "2026-06-01 00:00:00", id: 91);
        PostExtra(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES ({id}, 1, 'debit', 1, 92, 1);
            """, transactionDate: "2026-06-01", id: 92);

        var cash = Run(db).Where(row => row.AccountCode == "1100").ToList();

        // 日付として比べれば同着になり、伝票番号の小さいほうが先。
        // 文字列で比べると、時刻の無い 92 番が先に来る。
        Assert.Equal([1, 2, 4, 91, 92], cash.Select(row => row.EntryNo));

        // **累計も同じ順で積む**（窓の ORDER BY も `date()` を通している）。
        // 順が入れ替わると、途中の 2,791 が 2,792 になる。
        Assert.Equal([1000, 4000, 3700, 3791, 3883], cash.Select(row => row.RunningTotal));
    }

    /// <summary>
    /// 返った行を <c>&lt;伝票番号&gt;:&lt;借方&gt;/&lt;貸方&gt;</c> の集合にする（<b>並べ替える</b>）。
    /// </summary>
    /// <remarks>
    /// <b>件数ではなく、どの行が返ったかを表明するため</b>にある（qa/03 の L-46）。
    /// <b>並びは見ない</b>——元帳の並び（科目ごと・日付順・累計）は
    /// <c>累計は取引日の順に積み上がる</c> などが専門に見ている。
    /// **ここで並びまで固定すると、絞り込みのテストが並びの変更で落ちる**。
    /// </remarks>
    private static string Lines(IEnumerable<Row> rows)
        => string.Join(" ", rows
            .Select(row => $"{row.EntryNo}:{Amount(row.Debit)}/{Amount(row.Credit)}")
            .OrderBy(text => text, StringComparer.Ordinal));

    private static string Amount(long? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? "-";

    // --- 実行の土台 ---

    private static SqliteConnection Create()
    {
        var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, Accounts);
        TestDatabase.Execute(db, Book);
        // **摘要のない計上済みは、いまは作れない**（docs/10 §4-2-1）。
        // だが**規則より前に計上された行が稼働 DB に 2 件あり、計上済みは直せない**（I-05）。
        // 帳簿はその行も探せなければならない（電帳通達 8-13。「記録事項がない行を探せる」）ので、
        // **その 1 本だけ**トリガを外して作る。**外した定義は sqlite_master から読んで貼り直す。**
        const string PostingGuard = "trg_journal_entries_description_required_when_posted";
        TestDatabase.Execute(db, Post);
        TestDatabase.WithoutTrigger(db, PostingGuard, PostBlankDescription);

        // **摘要が空の計上済みは 2 番だけ**。検体に伝票を足した人が摘要を書き忘れたら、ここで鳴る。
        Assert.Equal(0L, TestDatabase.ScalarOf<long>(
            db,
            """
            SELECT COUNT(*) FROM journal_entries
             WHERE status = 'posted' AND id <> 2 AND (description IS NULL OR trim(description) = '')
            """));
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
        INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ('第 19 期の売上', (SELECT id FROM fiscal_years WHERE code = 'FY19'),
                    '2027-04-10', '2027-04-10', 'draft', 'normal', '2027-04-10 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (6, 1, 'debit', 1, 700, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (6, 2, 'credit', 2, 700, 1);

        -- 7) **取引日は第 18 期（2026-05-15）・計上日は第 19 期。** 決算後に見つかった取引。
        INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ('決算後に見つかった取引', (SELECT id FROM fiscal_years WHERE code = 'FY19'),
                    '2026-05-15', '2027-04-20', 'draft', 'normal', '2027-04-20 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 1, 'debit', 1, 100, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 2, 'credit', 2, 100, 1);

        -- 8) 第 17 期 2025-05-10 借 現金 50 / 貸 売上高 50。**いちばん古いのに id はいちばん大きい。**
        INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ('第 17 期の売上', (SELECT id FROM fiscal_years WHERE code = 'FY17'),
                    '2025-05-10', '2025-05-10', 'draft', 'normal', '2025-05-10 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 1, 'debit', 1, 50, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 2, 'credit', 2, 50, 1);

        -- 9) **第 19 期の減価償却費。** 費用科目が年度をまたぐ行はこれが唯一で、
        --    これが無いと `PARTITION BY` の `IN ('revenue', 'expense')` から
        --    **'expense' を落としても誰も赤くならない**（2026-09-14 の自己レビュー）。
        INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ('第 19 期の減価償却', (SELECT id FROM fiscal_years WHERE code = 'FY19'),
                    '2027-05-25', '2027-05-25', 'draft', 'normal', '2027-05-25 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (9, 1, 'debit', 6, 800, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (9, 2, 'credit', 3, 800, 1);

        -- 伝票番号は会計年度の中の連番（I-17）。年度が変われば 1 番から採り直す。
        UPDATE journal_entries SET status = 'posted', entry_no = 3, posted_at = '2027-05-25 10:00:00' WHERE id = 9;
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
        INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
            VALUES ('乙商事への売上', 1, '2026-05-30', '2026-05-30', 'draft', 'normal', '2026-05-30 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, partner_id, amount, tax_category_id)
            VALUES (6, 1, 'debit', 1, 2, 400, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (6, 2, 'credit', 2, 400, 1);

        -- 7) 05-31 借 現金 600 / 貸 売上高 600。**取引先 1 は伝票側**。
        INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, partner_id, entered_at)
            VALUES ('株式会社取引先への売上', 1, '2026-05-31', '2026-05-31', 'draft', 'normal', 1, '2026-05-31 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 1, 'debit', 1, 600, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (7, 2, 'credit', 2, 600, 1);

        -- 8) 7 番の取消。反対仕訳は原仕訳と同じ取引日・同じ取引先で、貸借を入れ替える（docs/10 §5）。
        INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, partner_id, entered_at)
            VALUES ('伝票番号 6 の取消: 株式会社取引先への売上', 1, '2026-05-31', '2026-06-01', 'draft', 'reversal', 7, 1, '2026-06-01 10:00:00');
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 1, 'debit', 2, 600, 1);
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (8, 2, 'credit', 1, 600, 1);

        UPDATE journal_entries SET status = 'posted', entry_no = 5, posted_at = '2026-05-30 10:00:00' WHERE id = 6;
        UPDATE journal_entries SET status = 'posted', entry_no = 6, posted_at = '2026-05-31 10:00:00' WHERE id = 7;
        UPDATE journal_entries SET status = 'posted', entry_no = 7, posted_at = '2026-06-01 10:00:00' WHERE id = 8;
        """;

    /// <summary>SQL が返す 16 列。<b>1 つも省かない</b>（省いた列は誰も見ていないことになる）。</summary>
    /// <remarks>
    /// <b>列の取り違えは、行まるごとの表明でしか捕まらない。</b>
    /// <c>QueryModuleTests</c> が見るのは<b>別名だけ</b>なので、
    /// <c>e.posting_date AS transaction_date</c> と書いても宣言とは一致する。
    /// <b>とくに <c>entry_id</c> は画面のリンク先</b>（`GeneralLedger.mod.json` の
    /// <c>IdVariable = EntryId.Value</c>）で、取り違えると<b>別の伝票が開く</b>。
    /// </remarks>
    private sealed record Row(
        long EntryId, int EntryNo, string AccountCode, string AccountName, string? SubAccountName,
        string FiscalYearLabel, string TransactionDate, string EntryType, string? CounterAccountName,
        string? DepartmentName, string? PartnerName, long? Debit, long? Credit, long RunningTotal,
        string? ItemDescription, string? Description);

    /// <summary>
    /// 元帳の SQL を<b>本物のまま</b>流す。渡さなかったパラメータは NULL（＝条件なし）。
    /// </summary>
    private static IReadOnlyList<Row> Run(SqliteConnection db, params (string Name, object? Value)[] parameters)
    {
        // **知らないパラメータ名を黙って捨てない。** 綴りを誤ると
        // **その条件が無いものとして流れ、「絞り込まれないこと」を見るテストが間違った理由で緑になる**。
        var unknown = parameters.Select(p => p.Name).Where(name => !Parameters.Contains(name)).ToList();
        Assert.True(
            unknown.Count == 0,
            $"この SQL に無いパラメータを渡している: {string.Join(" / ", unknown)}");

        using var command = db.CreateCommand();
        command.CommandText = TestDatabase.QuerySql("GeneralLedger");

        foreach (var name in Parameters)
        {
            // **片側だけ渡す検体があるので、値が null なら「渡していない」と同じに扱う。**
            var (_, givenValue) = parameters.FirstOrDefault(p => p.Name == name);
            command.Parameters.AddWithValue(name, givenValue ?? DBNull.Value);
        }

        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            rows.Add(new Row(
                reader.GetInt64(reader.GetOrdinal("entry_id")),
                reader.GetInt32(reader.GetOrdinal("entry_no")),
                reader.GetString(reader.GetOrdinal("account_code")),
                reader.GetString(reader.GetOrdinal("account_name")),
                Text(reader, "sub_account_name"),
                reader.GetString(reader.GetOrdinal("fiscal_year_label")),
                reader.GetString(reader.GetOrdinal("transaction_date")),
                reader.GetString(reader.GetOrdinal("entry_type")),
                Text(reader, "counter_account_name"),
                Text(reader, "department_name"),
                Text(reader, "partner_name"),
                Nullable(reader, "debit_amount") is int debit ? reader.GetInt64(debit) : null,
                Nullable(reader, "credit_amount") is int credit ? reader.GetInt64(credit) : null,
                reader.GetInt64(reader.GetOrdinal("running_total")),
                Text(reader, "item_description"),
                Text(reader, "description")));
        }

        return rows;
    }

    private static string? Text(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);

        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
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
