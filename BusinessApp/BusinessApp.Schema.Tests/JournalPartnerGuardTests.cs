namespace BusinessApp.Schema.Tests;

using BusinessApp.TestSupport;

using Microsoft.Data.Sqlite;

/// <summary>
/// 取引先を要する科目の明細は、取引先が無いままでは計上できない（docs/10 §6-2。docs/04 §1 の A-4）。
/// </summary>
/// <remarks>
/// <para><b>相手方を欠いた行は「相手方別」のどの帳簿にも載らない</b>（電帳規則 5 ① の括弧書き。docs/40 §4-1）。
/// 関門（<c>JournalEntryValidator</c> の <c>E-PARTNER-REQUIRED</c>）が本体で、ここは<b>関門が走らない経路</b>
/// （CSV 取込・<c>sql</c> CLI・手作業の SQL）への最後の守りである。</para>
/// <para><b>見るのは実効値である</b>——明細の取引先が空なら伝票のものが帳簿に載るので、
/// <b>伝票に 1 つ選んであれば足りる</b>（<c>JournalEntry.PartnerOf</c> と同じ規則）。</para>
/// <para><b>免除の形は補助科目の 2 値（<see cref="JournalSubAccountGuardTests"/>）とまったく同じ</b>——
/// 取消の、<b>計上済みの原仕訳を写しただけの明細</b>だけを外す。理由も同じで、
/// 取消の明細はサーバが原仕訳から作り、<b>利用者に直す手立てが無い</b>からである
/// （止めると規則より前の伝票を打ち消せなくなる。ADR-0004）。</para>
/// </remarks>
public class JournalPartnerGuardTests
{
    /// <summary>
    /// 売掛金（id 3）を足し、それを「取引先を要する」にする。
    /// </summary>
    /// <remarks>
    /// <b>科目 1・2 と別の科目を足すのは縮退を避けるため</b>（qa/03 L-02）——
    /// 「明細の科目」「要する科目」「相手方の科目」が全部同じだと、
    /// <c>a.id = l.account_id</c> の結び付けを落としても赤くならない。
    /// </remarks>
    private const string RequiresPartner = """
        INSERT INTO accounts (code, name, category) VALUES ('1300', '売掛金', 'asset');
        UPDATE accounts SET requires_partner = 1 WHERE code = '1300';
        INSERT INTO partners (code, name) VALUES ('P002', '別の取引先');
        """;

    /// <summary>取引先を要する科目の識別子（マスタの 2 件の後に採番される）。</summary>
    private const string ReceivableAccount = "3";

    /// <summary>上の <c>P002</c>。<b>1 件目（P001）を使わない</b>ので、識別子 1 での縮退を避けられる。</summary>
    private const string OtherPartner = "2";

    private const string Post =
        "UPDATE journal_entries SET status = 'posted', entry_no = 1, posted_at = '2026-05-20 10:00:00' WHERE id = 1";

    /// <summary>
    /// 売掛金の明細を持つ下書きを 1 件書く。取引先は明細・伝票のどちらにも置ける。
    /// </summary>
    /// <param name="linePartnerId">明細の取引先（<c>null</c> なら空）。</param>
    /// <param name="entryPartnerId">伝票の取引先（<c>null</c> なら空）。</param>
    /// <param name="entryType">伝票の種別。<c>normal</c> 以外では原仕訳（id 99）も作る。</param>
    /// <param name="mirrorPosted">原仕訳を計上済みにするか。<c>false</c> なら下書きのまま残す。</param>
    /// <param name="mirrorPartnerId">原仕訳の明細の取引先。<b>写しかどうかの判定に効く。</b></param>
    private static SqliteConnection Draft(
        string? linePartnerId,
        string? entryPartnerId = null,
        string entryType = "normal",
        bool mirrorPosted = true,
        string? mirrorPartnerId = null)
    {
        // **識別子を明示する**——原仕訳を先に入れる種別があるので、採番に任せると id が 1 でなくなる。
        var db = SchemaSeed.Create();
        TestDatabase.Execute(db, RequiresPartner);
        if (entryType != "normal")
        {
            // **原仕訳は計上済みで、同じ組み合わせの明細を持つ**（本番の形）。
            // トリガが外すのは「計上済みの原仕訳を写しただけの明細」だけなので、この明細が要る。
            TestDatabase.Execute(db, $"""
                INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                             description, entered_at)
                    VALUES (99, 1, '2026-05-19', '2026-05-19', 'draft', 'normal', '原仕訳', '2026-05-19 10:00:00');
                INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, partner_id, amount, tax_category_id)
                    VALUES (99, 1, 'debit', {ReceivableAccount}, {mirrorPartnerId ?? "NULL"}, 100000, 1);
                INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                    VALUES (99, 2, 'credit', 2, 2, 100000, 1);
                """);

            if (mirrorPosted)
            {
                // 原仕訳を計上済みにする。**規則より前に計上された伝票**を作るので、
                // このトリガだけ外す（TestDatabase の許可表に理由を書いてある）。
                TestDatabase.WithoutTrigger(
                    db,
                    "trg_journal_entries_partner_presence_when_posted",
                    "UPDATE journal_entries SET status = 'posted', entry_no = 9, posted_at = '2026-05-19 11:00:00' WHERE id = 99");
            }
        }

        TestDatabase.Execute(db, $"""
            INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                         description, entered_at, partner_id, original_entry_id)
                VALUES (1, 1, '2026-05-20', '2026-05-20', 'draft', '{entryType}', '5 月分の売上',
                        '2026-05-20 10:00:00', {entryPartnerId ?? "NULL"}, {(entryType == "normal" ? "NULL" : "99")});
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, partner_id, amount, tax_category_id)
                VALUES (1, 1, 'debit', {ReceivableAccount}, {linePartnerId ?? "NULL"}, 100000, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (1, 2, 'credit', 2, 2, 100000, 1);
            """);
        return db;
    }

    private static string StatusOf(SqliteConnection db)
        => TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 1");

    [Fact]
    public void 取引先を要する科目の明細に取引先が無ければ計上できない()
    {
        using var db = Draft(linePartnerId: null);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("取引先を要する勘定科目の明細には取引先が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 明細に取引先があれば計上できる()
    {
        using var db = Draft(OtherPartner);

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
        // **書いた値が残っていることまで見る**（qa/03 L-04）。
        Assert.Equal(2L, TestDatabase.ScalarOf<long>(
            db, "SELECT partner_id FROM journal_lines WHERE journal_entry_id = 1 AND line_no = 1"));
    }

    [Fact]
    public void 伝票に取引先があれば明細が空でも計上できる()
    {
        // **実効値で見る**——明細が空なら伝票の取引先が帳簿に載る（JournalEntry.PartnerOf）。
        // ここを明細だけで見ると、**帳簿には取引先が載る行を DB が拒む**ことになる。
        using var db = Draft(linePartnerId: null, entryPartnerId: OtherPartner);

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Fact]
    public void 取引先を要しない科目の明細は空のままでよい()
    {
        // **片側だけの規則である。** 売上高（科目 2）は要しないので、取引先が無くても通る。
        using var db = Draft(OtherPartner);
        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
        Assert.Equal(-1L, TestDatabase.ScalarOf<long>(
            db, "SELECT COALESCE(partner_id, -1) FROM journal_lines WHERE journal_entry_id = 1 AND line_no = 2"));
    }

    [Fact]
    public void 取消は取引先が無くても計上できる()
    {
        // **規則より前に計上された伝票を打ち消せなくなってはいけない**（docs/10 §5・ADR-0004）。
        // 稼働 DB に取引先の無い計上済み明細が 10 行ある（2026-09-08 実測）。
        using var db = Draft(linePartnerId: null, entryType: "reversal");

        TestDatabase.Execute(db, Post);

        Assert.Equal("posted", StatusOf(db));
    }

    [Fact]
    public void 取消と名乗るだけでは外れない()
    {
        // **entry_type は取込・CLI・手打ちの SQL が自由に書ける列である。**
        // 原仕訳の同じ科目の明細が取引先を持っていれば、この取消は写しではない。
        using var db = Draft(linePartnerId: null, entryType: "reversal", mirrorPartnerId: OtherPartner);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("取引先を要する勘定科目の明細には取引先が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 下書きの原仕訳をおとりにしても外れない()
    {
        // **原仕訳が計上済みであることまで見る。** journal_entries には
        // 「取消の原仕訳は計上済み」という制約が無いので、違反する下書きを 1 件立てて指せば
        // 規則を外せてしまう（qa/02 R53-03 と同じ型）。
        using var db = Draft(linePartnerId: null, entryType: "reversal", mirrorPosted: false);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("取引先を要する勘定科目の明細には取引先が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 訂正は取引先が無いと計上できない()
    {
        // **訂正の再計上は利用者が直せる**ので止める（JournalCorrectionPosting は明細を書き換えない）。
        // ここを外すと、訂正を経由して規則より後の違反を新しく帳簿へ入れられる（qa/02 R53-01）。
        using var db = Draft(linePartnerId: null, entryType: "correction");

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(db, Post));

        Assert.Contains("取引先を要する勘定科目の明細には取引先が要る", thrown.Message, StringComparison.Ordinal);
        Assert.Equal("draft", StatusOf(db));
    }

    [Fact]
    public void 別の伝票の違反明細は関係ない()
    {
        // **トリガは「この伝票の明細」だけを見る。** `l.journal_entry_id = NEW.id` を落とすと、
        // **違反明細を持つ伝票が 1 件でもある DB では、以後どの伝票も計上できなくなる**——
        // 稼働 DB がその形である（規則より前の 10 行）。
        using var db = Draft(linePartnerId: null, entryType: "reversal");
        TestDatabase.Execute(db, Post);

        // 違反明細を持つ取消（伝票 1）が計上済みのまま、正しい伝票を新しく計上する。
        TestDatabase.Execute(db, $"""
            INSERT INTO journal_entries (id, fiscal_year_id, transaction_date, posting_date, status, entry_type,
                                         description, entered_at)
                VALUES (2, 1, '2026-05-21', '2026-05-21', 'draft', 'normal', '別の伝票', '2026-05-21 10:00:00');
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, partner_id, amount, tax_category_id)
                VALUES (2, 1, 'debit', {ReceivableAccount}, {OtherPartner}, 500, 1);
            INSERT INTO journal_lines (journal_entry_id, line_no, debit_credit, account_id, department_id, amount, tax_category_id)
                VALUES (2, 2, 'credit', 2, 2, 500, 1);
            UPDATE journal_entries SET status = 'posted', entry_no = 2, posted_at = '2026-05-21 11:00:00' WHERE id = 2;
            """);

        Assert.Equal("posted", TestDatabase.ScalarOf<string>(db, "SELECT status FROM journal_entries WHERE id = 2"));
    }

    [Fact]
    public void 計上済みの違反伝票を触ると計上済みの断りが出る()
    {
        // **OLD.status を見ていないと、規則より前に計上された伝票を触ったときにこのトリガが鳴り、**
        // **本来出るべき「計上済みの仕訳は変更できない」を隠す**（0015 が摘要で直したのと同じ型）。
        using var db = Draft(linePartnerId: null, entryType: "reversal");
        TestDatabase.Execute(db, Post);

        var thrown = Assert.Throws<SqliteException>(() => TestDatabase.Execute(
            db, "UPDATE journal_entries SET description = '触った' WHERE id = 1"));

        Assert.Contains("計上済みの仕訳は変更できない", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 下書きの伝票は触れる()
    {
        // **NEW.status を落とすと、下書きの保存まで止まる。**
        using var db = Draft(linePartnerId: null);

        TestDatabase.Execute(db, "UPDATE journal_entries SET description = '書き直した' WHERE id = 1");

        Assert.Equal("書き直した", TestDatabase.ScalarOf<string>(db, "SELECT description FROM journal_entries WHERE id = 1"));
        Assert.Equal("draft", StatusOf(db));
    }
}
