namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// I-05 計上済み仕訳は変更も削除もされない（ADR-0004）。
/// </summary>
/// <remarks>
/// AccountingCore の検証とサーバの関門が第一の防波堤だが、これは
/// <b>CSV 取込・API・スクリプト・手作業の SQL のどれからでも通る最後の関門</b>である。
/// 「規則を迂回する経路を作らない」を、規約ではなく DB に守らせていることを確かめる。
/// </remarks>
public class JournalImmutabilityTests
{
    [Fact]
    public void 下書きを作り明細を入れて計上できる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        Assert.Equal("posted", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT entry_no FROM journal_entries WHERE id = 1"));
        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 1"));
    }

    [Fact]
    public void 下書きは自由に編集できる()
    {
        using var db = TestDatabase.Create();
        TestDatabase.Execute(db, SchemaSeed.Masters);
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 100, 1);
            UPDATE journal_lines SET amount = 200 WHERE id = 1;
            UPDATE journal_entries SET description = '書き直した' WHERE id = 1;
            DELETE FROM journal_lines WHERE id = 1;
            DELETE FROM journal_entries WHERE id = 1;
            """);

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries"));
    }

    /// <remarks>
    /// <b>どのトリガが鳴ったかまで表明する。</b> 例外の型だけだと、
    /// <c>DELETE FROM journal_entries</c> は<b>トリガを外しても子明細の外部キーが代わりに拒む</b>ので、
    /// 「見張っている」ことの証拠にならない（2026-09-14 の制約ノックアウトで生き残った。qa/03 の L-45）。
    /// </remarks>
    [Theory]
    [InlineData("UPDATE journal_entries SET description = '改ざん' WHERE id = 1", "計上済みの仕訳は変更できない。")]
    [InlineData("UPDATE journal_entries SET status = 'draft' WHERE id = 1", "計上済みの仕訳は変更できない。")]
    [InlineData("DELETE FROM journal_entries WHERE id = 1", "計上済みの仕訳は削除できない。")]
    // **その場の更新は「移動できない」のほうが鳴る。** どちらのトリガも WHEN が真になり
    // （OLD も NEW も同じ計上済みの伝票）、**後に作られたほうから鳴る**（発火順は SQLite の仕様上 undefined。
    // 実測は 2026-09-14）。**`trg_journal_lines_posted_no_update` を単独で撃つ検体は下にある。**
    [InlineData("UPDATE journal_lines SET amount = 1 WHERE journal_entry_id = 1", "計上済みの仕訳へ明細を移動できない。")]
    [InlineData("UPDATE journal_lines SET account_id = 2 WHERE journal_entry_id = 1", "計上済みの仕訳へ明細を移動できない。")]
    [InlineData("DELETE FROM journal_lines WHERE journal_entry_id = 1", "計上済みの仕訳明細は削除できない。")]
    public void 計上済みの仕訳は変更も削除もできない(string sql, string message)
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        Rejected.ByTrigger(db, sql, message);
    }

    /// <summary>
    /// <b>計上済みの伝票から明細を引き抜けない</b>（<c>trg_journal_lines_posted_no_update</c>）。
    /// </summary>
    /// <remarks>
    /// <b>このトリガを単独で撃てるのは、この形だけである。</b> その場の更新では
    /// <c>trg_journal_lines_no_move_into_posted</c> が先に鳴って隠れてしまう（上の検体）。
    /// <b>行き先を下書きにすると、NEW 側の WHEN が偽になり、OLD 側だけが残る。</b>
    /// </remarks>
    [Fact]
    public void 計上済みの伝票から明細を下書きへ移せない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('6 月分の現金売上', 1, '2026-06-20', '2026-06-20', 'draft', 'normal', '2026-06-20 10:00:00');
            """);

        Rejected.ByTrigger(
            db,
            """
            UPDATE journal_lines SET journal_entry_id = (SELECT MAX(id) FROM journal_entries)
             WHERE journal_entry_id = 1 AND line_no = 1;
            """,
            "計上済みの仕訳明細は変更できない。");
    }

    [Fact]
    public void 計上済みの仕訳に明細を追加できない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 3, 'debit', 1, 1, 1);
            """));
    }

    /// <summary>
    /// <b>下書きの明細を、計上済みの伝票へ付け替えられない。</b>
    /// </summary>
    /// <remarks>
    /// 上の 2 本のトリガは <c>OLD</c> の伝票（＝いま所属している伝票）しか見ないので、
    /// <b>この経路だけが 2026-09-03 まで開いていた</b>（qa/03 L-25）。通ると、計上済みの伝票に
    /// 身に覚えのない行が増える——貸借一致（I-01）も不変性（I-05）もその瞬間に破れ、
    /// しかも計上済みなので訂正も取消もできない行が恒久的に残る。
    /// </remarks>
    [Fact]
    public void 下書きの明細を計上済みの伝票へ移せない()
    {
        using var db = WithPostedAndDraft();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE journal_lines SET journal_entry_id = 1 WHERE journal_entry_id = 2"));

        // **どの制約で落ちたかまで見る。** 例外の型だけだと、seed の形が変わって
        // 別の制約（UNIQUE(journal_entry_id, line_no) など）に当たっても緑のままになる。
        Assert.Contains("計上済みの仕訳へ明細を移動できない", thrown.Message, StringComparison.Ordinal);

        // 計上済みは増えず、下書きの側も残っている（＝文が丸ごと巻き戻った）。
        Assert.Equal(2L, TestDatabase.ScalarOf<long>(
            db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 1"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(
            db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 2"));
    }

    [Fact]
    public void 計上済みの明細を下書きへ逃がせない()
    {
        using var db = WithPostedAndDraft();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE journal_lines SET journal_entry_id = 2 WHERE journal_entry_id = 1"));

        Assert.Contains("計上済みの仕訳明細は変更できない", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>REPLACE の暗黙の DELETE で、計上済みが音もなく消えない。</b>
    /// </summary>
    /// <remarks>
    /// SQLite は、REPLACE が制約充足のために消す行の DELETE トリガを
    /// <c>PRAGMA recursive_triggers</c> が OFF のあいだ発火しない（既定は OFF）。
    /// <b>接続の設定に頼らず、衝突そのものを拒む</b>（qa/03 L-26）。
    /// </remarks>
    [Theory]
    [InlineData(
        "INSERT OR REPLACE INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at) "
        + "VALUES (1, 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00')",
        "計上済みの仕訳を上書きできない")]
    [InlineData(
        "UPDATE OR REPLACE journal_entries SET idempotency_key = 'K1' WHERE id = 2",
        "計上済みの仕訳を上書きできない")]
    [InlineData(
        "INSERT OR REPLACE INTO journal_lines (id, journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id) "
        + "VALUES (1, 2, 5, 'debit', 1, 7, 1)",
        "計上済みの仕訳明細を上書きできない")]
    [InlineData(
        "UPDATE OR REPLACE journal_lines SET id = 1 WHERE id = 3",
        "計上済みの仕訳明細を上書きできない")]
    public void REPLACEで計上済みを置き換えられない(string sql, string message)
    {
        using var db = WithPostedAndDraft();

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, sql));

        Assert.Contains(message, thrown.Message, StringComparison.Ordinal);
        Assert.Equal("posted", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1"));
        Assert.Equal(2L, TestDatabase.ScalarOf<long>(
            db, "SELECT COUNT(*) FROM journal_lines WHERE journal_entry_id = 1"));
    }

    /// <summary>
    /// 計上済みの伝票 1 件（id = 1・明細 2 行・冪等キー <c>K1</c>）と、
    /// 下書き 1 件（id = 2・明細 1 行＝id 3）。
    /// </summary>
    private static SqliteConnection WithPostedAndDraft()
    {
        var db = SchemaSeed.CreateWithPostedEntry();
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (2, 9, 'credit', 1, 999, 1);
            """);

        // 冪等キーは計上済みに付ける。計上済みは UPDATE できないので、トリガを外して書く
        // ——**この経路はテストの都合であって、製品には無い**。
        TestDatabase.Execute(db, """
            DROP TRIGGER trg_journal_entries_posted_no_update;
            UPDATE journal_entries SET idempotency_key = 'K1' WHERE id = 1;
            CREATE TRIGGER trg_journal_entries_posted_no_update
            BEFORE UPDATE ON journal_entries
            FOR EACH ROW WHEN OLD.status = 'posted'
            BEGIN
                SELECT RAISE(ABORT, '計上済みの仕訳は変更できない。訂正・取消は反対仕訳で行う。');
            END;
            """);

        return db;
    }

    /// <summary>訂正・取消は反対仕訳で行う。原仕訳は残したまま新しい伝票が増える。</summary>
    [Fact]
    public void 取消は反対仕訳として別の伝票になる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-21', '2026-05-21', 'draft', 'reversal', 1, '2026-05-21 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (2, 1, 'credit', 1, 100000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (2, 2, 'debit', 2, 2, 100000, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-21 10:00:00' WHERE id = 2;
            """);

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries"));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db, "SELECT original_entry_id FROM journal_entries WHERE id = 2"));
    }

    /// <summary>
    /// 計上は「下書きとして書いてから状態を進める」経路しか無い。
    /// </summary>
    /// <remarks>
    /// ここが開いていると、貸借不一致・明細ゼロの計上済み伝票を直接書き込めてしまう。
    /// しかも他のトリガが UPDATE も DELETE も明細の追加も止めるので、
    /// <b>訂正も取消もできない行が恒久的に残る</b>（ADR-0004 が最も避けたい状態）。
    /// </remarks>
    [Fact]
    public void 最初から計上済みとして仕訳を作れない()
    {
        using var db = SchemaSeed.Create();

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, entry_no, transaction_date, posting_date, status, entry_type, entered_at, posted_at)
                VALUES ('5 月分の現金売上', 1, 99, '2026-05-20', '2026-05-20', 'posted', 'normal', '2026-05-20 10:00:00', '2026-05-20 10:00:00');
            """));

        Assert.Equal(0L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries"));
    }

    /// <summary>
    /// 金額は整数円でなければならない（docs/10 §3）。
    /// </summary>
    /// <remarks>
    /// <b>INTEGER と書くだけでは整数にならない。</b> SQLite の型親和性は 100.5 を整数に落とせず、
    /// REAL のまま格納する。貸借一致の判定と保存値がずれる（I-01）ので DB でも拒む。
    /// </remarks>
    [Fact]
    public void 小数の金額は保存できない()
    {
        using var db = SchemaSeed.Create();

        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            """);

        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, """
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', 1, 100.5, 1);
            """));
    }

    /// <summary>
    /// 1 本の仕訳を取り消す反対仕訳は 1 本まで。
    /// </summary>
    /// <remarks>
    /// アプリも計上前に検査するが、同時に 2 人が取り消すと両方が「まだ取り消されていない」を
    /// 読んでしまう。**二重取消が通ると残高が原仕訳 1 本分ずれる。**
    /// </remarks>
    [Fact]
    public void 同じ仕訳を二度取り消せない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, Reversal(id: 2, entryNo: 2));

        // 止まるのは 2 本目を**計上する**ところ。下書きのままなら作れる。
        Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Reversal(id: 3, entryNo: 3)));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM journal_entries WHERE entry_type = 'reversal' AND status = 'posted'"));
    }

    /// <summary>下書きのままなら何本でも作れる（計上した 1 本だけが帳簿に載る）。</summary>
    [Fact]
    public void 取消の下書きは何本でも作れる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, ReversalDraft(2) + ReversalDraft(3));

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db, "SELECT COUNT(*) FROM journal_entries WHERE entry_type = 'reversal'"));
    }

    /// <summary>
    /// 1 本の仕訳を訂正する再計上も 1 本まで（ADR-0015）。
    /// </summary>
    /// <remarks>
    /// <b>再計上が 2 本載ると、直した内容がそのまま二重に計上される。</b>
    /// 取消と同じく、アプリ側の検査は同時に 2 人が訂正した場合に勝てない。
    /// </remarks>
    [Fact]
    public void 同じ仕訳を二度訂正できない()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, Amendment(id: 2, entryNo: 2, entryType: "reversal"));
        TestDatabase.Execute(db, Amendment(id: 3, entryNo: 3, entryType: "correction"));

        Assert.Throws<SqliteException>(() =>
            TestDatabase.Execute(db, Amendment(id: 4, entryNo: 4, entryType: "correction")));
        Assert.Equal(1L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM journal_entries WHERE entry_type = 'correction' AND status = 'posted'"));
    }

    /// <summary>
    /// 取消と再計上は別々に数える。同じ原仕訳に 1 本ずつ載るのが訂正の正常な姿である。
    /// </summary>
    [Fact]
    public void 取消と再計上は同じ原仕訳に一本ずつ載せられる()
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        TestDatabase.Execute(db, Amendment(id: 2, entryNo: 2, entryType: "reversal"));
        TestDatabase.Execute(db, Amendment(id: 3, entryNo: 3, entryType: "correction"));

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db,
            "SELECT COUNT(*) FROM journal_entries WHERE original_entry_id = 1 AND status = 'posted'"));
    }

    /// <summary>
    /// 一意なのは「原仕訳ごと」であって「帳簿全体で 1 本」ではない。
    /// </summary>
    /// <remarks>
    /// インデックスの列を <c>(original_entry_id)</c> から <c>(entry_type)</c> に取り違えても、
    /// 原仕訳が 1 本しか無いテストでは緑のままになる。症状は
    /// 「帳簿全体で訂正が 1 本しか計上できない」で、最初の 1 件は通るぶん発見が遅れる。
    /// </remarks>
    [Theory]
    [InlineData("reversal")]
    [InlineData("correction")]
    public void 別々の原仕訳ならそれぞれ足せる(string entryType)
    {
        using var db = SchemaSeed.CreateWithPostedEntry();

        // 2 本目の原仕訳を作って計上する。
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (description, id, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 10, 1, '2026-05-20', '2026-05-20', 'draft', 'normal', '2026-05-20 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
                VALUES (10, 1, 'debit', 1, 500, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 9, posted_at = '2026-05-20 10:00:00' WHERE id = 10;
            """);

        TestDatabase.Execute(db, Amendment(id: 2, entryNo: 2, entryType: entryType));
        TestDatabase.Execute(db, Amendment(id: 3, entryNo: 3, entryType: entryType, originalEntryId: 10));

        Assert.Equal(2L, TestDatabase.ScalarOf<long>(db,
            $"SELECT COUNT(*) FROM journal_entries WHERE entry_type = '{entryType}' AND status = 'posted'"));
    }

    /// <summary>
    /// <b>I-02 計上済みの仕訳は、帳簿ぜんたいで数えても借方合計＝貸方合計。</b>
    /// </summary>
    /// <remarks>
    /// <para><b>I-01（伝票ごとの貸借一致）の帰結だが、破れ方が違う。</b>
    /// 伝票ごとに合っていても、<b>明細が伝票をまたいで動いた・計上済みの行が消えた</b>ときは
    /// <b>帳簿ぜんたいの合計だけが狂う</b>——I-01 を見るテストは 1 本も赤くならない。</para>
    /// <para><b>これを見張っているのは不変性のトリガ（I-05）である。</b>
    /// 貸借一致そのものに DB 側の担保は無い（行をまたぐので <c>CHECK</c> では書けない。
    /// <c>Designer/ddl/README.md</c> の二重防御の表）。だから
    /// <b>「合計を数えるだけ」のテストは何も見張っていない</b>——自分で入れた行を自分で数え直すだけになる
    /// （<b>最初にそう書いた。</b>2026-09-14 の自己レビューで気づいた。qa/02 のラウンド 90）。
    /// <b>断られる経路を 1 本ずつ撃ち、断られたあとに合計が動いていないことまで見る。</b></para>
    /// <para><b>検体は、通ってしまったら合計が必ず狂う形にしてある</b>——
    /// 下書きの明細は 999 円で、計上済みの 100,000 円と重ならない。
    /// <b>どのトリガを 1 本外しても、この 1 本が赤くなる。</b></para>
    /// </remarks>
    [Theory]
    // 下書きの明細を計上済みへ付け替える（通れば貸方だけが 100,999 になる）。
    [InlineData(
        "UPDATE journal_lines SET journal_entry_id = 1 WHERE journal_entry_id = 2",
        "計上済みの仕訳へ明細を移動できない。")]
    // 計上済みの明細を下書きへ逃がす（通れば借方だけが 0 になる）。
    [InlineData(
        "UPDATE journal_lines SET journal_entry_id = 2 WHERE journal_entry_id = 1 AND line_no = 1",
        "計上済みの仕訳明細は変更できない。")]
    // 計上済みの明細を消す（通れば貸方だけが 0 になる）。
    [InlineData(
        "DELETE FROM journal_lines WHERE journal_entry_id = 1 AND line_no = 2",
        "計上済みの仕訳明細は削除できない。")]
    // 金額をその場で書き換える。**鳴るのは「移動できない」のほう**——
    // どちらのトリガも WHEN が真になり、発火順は SQLite の仕様上 undefined である（上の Theory と同じ）。
    [InlineData(
        "UPDATE journal_lines SET amount = 1 WHERE journal_entry_id = 1 AND line_no = 1",
        "計上済みの仕訳へ明細を移動できない。")]
    // 計上済みの伝票に明細を足す（通れば借方だけが増える）。
    [InlineData(
        """
        INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, amount, tax_category_id)
            VALUES (1, 3, 'debit', 1, 777, 1);
        """,
        "計上済みの仕訳に明細を追加できない。")]
    // 計上済みの伝票ごと消す（通れば両方 0 になる）。
    [InlineData(
        "DELETE FROM journal_entries WHERE id = 1",
        "計上済みの仕訳は削除できない。")]
    public void 計上済みの仕訳は帳簿ぜんたいでも貸借が一致する(string sql, string message)
    {
        using var db = WithPostedAndDraft();

        Rejected.ByTrigger(db, sql, message);

        var debit = TestDatabase.ScalarOf<long>(db, PostedTotalOf("debit"));
        var credit = TestDatabase.ScalarOf<long>(db, PostedTotalOf("credit"));

        // **0 と 0 の一致は何も言っていない。** 計上済みの明細が残っていることまで見る。
        Assert.Equal(PostedAmount, debit);
        Assert.Equal(debit, credit);
    }

    /// <summary><see cref="SchemaSeed.Draft"/> が入れる計上済みの明細 1 行の金額。</summary>
    private const long PostedAmount = 100000L;

    private static string PostedTotalOf(string debitCredit) => $"""
        SELECT COALESCE(SUM(l.amount), 0) FROM journal_lines l
          JOIN journal_entries e ON e.id = l.journal_entry_id
         WHERE e.status = 'posted' AND l.debit_credit = '{debitCredit}';
        """;

    private static string ReversalDraft(int id) => AmendmentDraft(id, "reversal");

    private static string Reversal(int id, int entryNo) => Amendment(id, entryNo, "reversal");

    private static string AmendmentDraft(int id, string entryType, int originalEntryId = 1) => $"""
        INSERT INTO journal_entries (description, id, fiscal_year_id, transaction_date, posting_date, status, entry_type, original_entry_id, entered_at)
            VALUES ('5 月分の現金売上', {id}, 1, '2026-05-20', '2026-05-21', 'draft', '{entryType}', {originalEntryId}, '2026-05-21 10:00:00');
        """;

    private static string Amendment(int id, int entryNo, string entryType, int originalEntryId = 1)
        => $"""
        {AmendmentDraft(id, entryType, originalEntryId)}
        UPDATE journal_entries SET status = 'posted', entry_no = {entryNo}, posted_at = '2026-05-21 10:00:00' WHERE id = {id};
        """;

    /// <summary>
    /// <b>入力年月日は下書きの間も変えられない</b>（<c>trg_journal_entries_entered_at_immutable</c>）。
    /// </summary>
    /// <remarks>
    /// 「通常の業務処理期間の経過後に入力した事実を確認できる」という優良な電子帳簿の要件は、
    /// <b>この値が動かないことで初めて成り立つ</b>（根拠の条番号は DDL の注記が持つ）。
    /// <b>2026-09-13 の制約ノックアウトの初回掃引で「誰も見張っていない」と出た</b>（qa/02 のラウンド 88）。
    /// </remarks>
    [Fact]
    public void 入力年月日は下書きでも変えられない()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);

        Rejected.ByTrigger(
            db,
            "UPDATE journal_entries SET entered_at = '2026-06-01 10:00:00' WHERE id = 1;",
            "入力年月日は変更できない。");
    }

    /// <summary>
    /// <b>仕訳の種別は変えられない</b>（<c>trg_journal_entries_entry_type_immutable</c>）。
    /// </summary>
    /// <remarks>
    /// 変えられると、<b>通常の伝票を後から別の意味に化けさせる</b>経路ができる。
    /// 種別を変えたいときは下書きを作り直す。
    ///
    /// <b>検体に <c>reversal</c> を使わない。</b> 原仕訳を伴わない <c>reversal</c> は
    /// <c>CHECK (entry_type NOT IN ('correction', 'reversal') OR original_entry_id IS NOT NULL)</c> が
    /// 先に弾くので、<b>トリガを外しても赤いまま</b>になり、このトリガを見張ったことにならない
    /// （2026-09-13 に掃引で確かめた）。原仕訳を要さない種別で撃つ。
    /// </remarks>
    [Fact]
    public void 仕訳の種別は下書きでも変えられない()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);

        Rejected.ByTrigger(
            db,
            "UPDATE journal_entries SET entry_type = 'opening' WHERE id = 1;",
            "仕訳の種別は変更できない。");
    }

    /// <summary>
    /// <b>原仕訳を指せるのは訂正・取消だけ</b>（I-06 の逆向き）。
    /// </summary>
    /// <remarks>
    /// <b>通常の伝票が原仕訳を持てると、「取り消された」の判定が壊れる</b>——
    /// 取消でない伝票が原仕訳を指しているだけで、元の伝票が取消済みに見えてしまう。
    /// <b>トリガは INSERT と UPDATE で 2 本あるので検体も 2 つ要る</b>
    /// （qa/03 の L-44 と同じ型——同じ規則を 2 つの経路に当てるときは、対で持つ）。
    /// </remarks>
    [Fact]
    public void 通常の伝票は原仕訳を指せない()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);

        Rejected.ByTrigger(
            db,
            """
            INSERT INTO journal_entries (description, fiscal_year_id, original_entry_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES ('5 月分の現金売上', 1, 1, '2026-05-21', '2026-05-21', 'draft', 'normal', '2026-05-21 10:00:00');
            """,
            "原仕訳を指定できるのは訂正・取消だけ。");
    }

    /// <summary>上と対。<b>後から原仕訳を付けても止まる。</b></summary>
    /// <remarks>
    /// <b>自分自身を指す検体は使わない。</b> <c>CHECK (original_entry_id IS NULL OR original_entry_id &lt;&gt; id)</c> が
    /// 先に弾くので、<b>トリガを外しても赤いまま</b>になる（2026-09-13 に掃引で確かめた）。
    /// 別の伝票を指す。
    /// </remarks>
    [Fact]
    public void 通常の伝票に後から原仕訳を付けられない()
    {
        using var db = SchemaSeed.Create();
        TestDatabase.Execute(db, SchemaSeed.Draft);
        TestDatabase.Execute(db, """
            INSERT INTO journal_entries (id, description, fiscal_year_id, transaction_date, posting_date, status, entry_type, entered_at)
                VALUES (2, '6 月分の現金売上', 1, '2026-06-20', '2026-06-20', 'draft', 'normal', '2026-06-20 10:00:00');
            """);

        Rejected.ByTrigger(
            db,
            "UPDATE journal_entries SET original_entry_id = 1 WHERE id = 2;",
            "原仕訳を指定できるのは訂正・取消だけ。");
    }
}
